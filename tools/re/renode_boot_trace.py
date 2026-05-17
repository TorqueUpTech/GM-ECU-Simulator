"""Renode boot-from-reset trace.

Goal: start at flash 0x00000000 (MPC5566 BAM entry) and step the
firmware long enough to either:
  (a) reach the OSAF init that copies 0x9655 to BaOSAF_SecurKey, or
  (b) get stuck in a known place we can identify and stub past.

Strategy: step in batches, sample PC, and log a summary of where the
CPU is spending its time. Watch the OSAF RAM region for any write -
that's the answer to "how does the key get into RAM" if we ever see it.
"""

from __future__ import annotations
import socket, time, sys, re, subprocess
from collections import Counter
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

PORT          = 12350
BIN_PATH      = "c:/Tools/Renode/bins/e38.bin"
PLATFORM_REPL = "c:/Tools/Renode/mpc5566_e38_full.repl"
RENODE_EXE    = r"C:\Tools\Renode\renode_1.16.1-dotnet_portable\renode.exe"

# Watch addresses
ADDR_KEY_FLASH   = 0x0000C134
ADDR_KEY_RAM     = 0x003F8614
ADDR_SEED_RAM    = 0x003F861C
ADDR_LOCK_RAM    = 0x00403990

# Boot from reset. MPC5566 BAM enters at flash 0x0.
ENTRY_PC = 0x00000000

HEX_RE = re.compile(r"^0x[0-9A-Fa-f]+$")


class RenodeClient:
    def __init__(self, port):
        self.s = socket.create_connection(("127.0.0.1", port), timeout=10)
        self.s.settimeout(2.5)
        try: self.s.recv(8192)
        except socket.timeout: pass

    def cmd(self, c, wait=0.3):
        self.s.sendall((c + "\n").encode())
        time.sleep(wait)
        out = []
        try:
            while True:
                ch = self.s.recv(16384).decode(errors="replace")
                if not ch: break
                out.append(ch)
                if ch.rstrip().endswith(")"): break
        except socket.timeout:
            pass
        return "".join(out)

    def val(self, c):
        for line in self.cmd(c).splitlines():
            line = line.strip()
            if HEX_RE.match(line):
                return int(line, 16)
        return None

    def close(self): self.s.close()


def page(addr):
    """Categorize an address into a memory region."""
    if addr < 0x10000: return "boot"
    if addr < 0x1C0000: return "OS"
    if addr < 0x200000: return "cal"
    if 0x40000000 <= addr < 0x40020000: return "SRAM"
    if 0x003F8000 <= addr < 0x0041A000: return "ramA2L"
    if 0xC3F00000 <= addr < 0xC4000000: return "pbridgeA"
    if 0xFFE00000 <= addr <= 0xFFFFFFFF: return "pbridgeB"
    return f"other(0x{addr & ~0xFFFFF:X}+)"


def main():
    proc = subprocess.Popen(
        [RENODE_EXE, "--plain", "--disable-gui", "--hide-monitor",
         "--hide-log", "--port", str(PORT)],
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
    )
    time.sleep(6)
    try:
        r = RenodeClient(PORT)
        print("=== Setup ===")
        print(r.cmd('mach create "ecm"'))
        print(r.cmd(f'machine LoadPlatformDescription @{PLATFORM_REPL}'))
        print(r.cmd(f'sysbus LoadBinary @{BIN_PATH} 0x00000000'))

        print("\n=== Snapshot interesting addresses BEFORE boot ===")
        for n, a in [("key flash", ADDR_KEY_FLASH),
                     ("key RAM",  ADDR_KEY_RAM),
                     ("seed RAM", ADDR_SEED_RAM),
                     ("lock RAM", ADDR_LOCK_RAM)]:
            v = r.val(f'sysbus ReadDoubleWord 0x{a:08X}')
            print(f"  {n:10s} @0x{a:08X} = 0x{v:08X}")

        print(f"\n=== Set PC = 0x{ENTRY_PC:08X} (MPC5566 BAM entry) and start stepping ===")
        r.cmd(f'sysbus.cpu PC 0x{ENTRY_PC:08X}')
        # Don't pre-set r13/r2; let the firmware initialise them itself.

        BUDGET = 2_000_000
        CHUNK  = 5_000
        steps = 0
        pc_pages = Counter()
        last_pc = ENTRY_PC
        stuck_counter = 0
        first_boot_exit = None

        while steps < BUDGET:
            r.cmd(f'sysbus.cpu Step {CHUNK}', wait=0.5)
            steps += CHUNK
            pc = r.val('sysbus.cpu PC')
            if pc is None:
                print(f"  [no PC at step {steps}]")
                break
            cat = page(pc)
            pc_pages[cat] += 1
            if cat != "boot" and first_boot_exit is None:
                first_boot_exit = (steps, pc)
            if pc == last_pc:
                stuck_counter += 1
                if stuck_counter >= 3:
                    print(f"  step {steps:>8d}: PC=0x{pc:08X}  STUCK in tight loop")
                    break
            else:
                stuck_counter = 0
            if steps % 50_000 == 0 or stuck_counter:
                print(f"  step {steps:>8d}: PC=0x{pc:08X} ({cat})")
            last_pc = pc

            # Check the OSAF RAM for any change
            key = r.val(f'sysbus ReadDoubleWord 0x{ADDR_KEY_RAM:08X}')
            if key not in (0, None):
                print(f"  >> BaOSAF_SecurKey at 0x{ADDR_KEY_RAM:08X} CHANGED to 0x{key:08X} at step {steps}!")
                break

        print("\n=== Boot-region exit ===")
        if first_boot_exit:
            print(f"  PC left the boot region at step {first_boot_exit[0]:,}, PC=0x{first_boot_exit[1]:08X}")
        else:
            print(f"  PC never left the boot region after {steps:,} steps")

        print("\n=== Where the CPU spent its time (PC-page sample histogram) ===")
        total = sum(pc_pages.values()) or 1
        for cat, count in pc_pages.most_common():
            print(f"  {cat:12s}  {count:5d}  ({100.0 * count / total:.1f}%)")

        print("\n=== Final state of watched addresses ===")
        for n, a in [("key flash", ADDR_KEY_FLASH),
                     ("key RAM",  ADDR_KEY_RAM),
                     ("seed RAM", ADDR_SEED_RAM),
                     ("lock RAM", ADDR_LOCK_RAM)]:
            v = r.val(f'sysbus ReadDoubleWord 0x{a:08X}')
            mark = "  <- CHANGED" if (n.endswith("RAM") and v not in (0, None)) else ""
            print(f"  {n:10s} @0x{a:08X} = 0x{v:08X}{mark}")

        # Final PC + register dump
        final_pc = r.val('sysbus.cpu PC')
        r2 = r.val('sysbus.cpu GetRegisterUnsafe 2')
        r13 = r.val('sysbus.cpu GetRegisterUnsafe 13')
        print(f"\n  final PC = 0x{final_pc:08X}  r2 = 0x{r2:08X}  r13 = 0x{r13:08X}")
        print(f"  expected at steady state: r2=0x0001CED0, r13=0x00400000")

        r.close()
    finally:
        proc.terminate()
        try: proc.wait(timeout=5)
        except subprocess.TimeoutExpired: proc.kill()


if __name__ == "__main__":
    main()
