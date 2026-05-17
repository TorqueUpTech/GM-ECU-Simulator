"""Renode boot trace v2: start at FUN_00014EE0 (OS startup), let it
run as far as it can, watch for any write to OSAF key/seed RAM
locations or any read of flash 0xC134.

Strategy: use single-step in a loop, sample state periodically, stop
on observed key/seed activity or after a time budget.
"""

from __future__ import annotations
import socket, time, sys, re, subprocess
from collections import Counter
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

PORT          = 12357
BIN_PATH      = "c:/Tools/Renode/bins/e38.bin"
PLATFORM_REPL = "c:/Tools/Renode/mpc5566_e38_full.repl"
RENODE_EXE    = r"C:\Tools\Renode\renode_1.16.1-dotnet_portable\renode.exe"

ENTRY_PC = 0x00014EE0   # OS startup function (calls FUN_000333D8 for SDA init)

# RAM addresses to poll for change
WATCH_RAM = [
    ("key (RAM)",  0x003F8614),
    ("seed (RAM)", 0x003F861C),
    ("lock (RAM)", 0x00403990),
    ("key2 (RAM)", 0x003F8618),  # BaOSAF_SecurKeyDVT
]

HEX_RE = re.compile(r"^0x[0-9A-Fa-f]+$")


class RenodeClient:
    def __init__(self, port):
        self.s = socket.create_connection(("127.0.0.1", port), timeout=15)
        self.s.settimeout(3)
        try: self.s.recv(8192)
        except socket.timeout: pass

    def cmd(self, c, wait=0.1):
        self.s.sendall((c + "\n").encode())
        time.sleep(wait)
        out = []
        try:
            while True:
                ch = self.s.recv(16384).decode(errors="replace")
                if not ch: break
                out.append(ch)
                if ch.rstrip().endswith(")"): break
        except socket.timeout: pass
        return "".join(out)

    def val(self, c):
        for line in self.cmd(c).splitlines():
            line = line.strip()
            if HEX_RE.match(line): return int(line, 16)
        return None

    def close(self): self.s.close()


def main():
    proc = subprocess.Popen(
        [RENODE_EXE, "--plain", "--disable-gui", "--hide-monitor",
         "--hide-log", "--port", str(PORT)],
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
    )
    time.sleep(7)
    try:
        r = RenodeClient(PORT)
        print("=== Setup ===")
        r.cmd('mach create "ecm"', wait=0.3)
        r.cmd(f'machine LoadPlatformDescription @{PLATFORM_REPL}', wait=1.0)
        r.cmd(f'sysbus LoadBinary @{BIN_PATH} 0x00000000', wait=1.0)

        # snapshot initial RAM
        initial = {}
        for n, a in WATCH_RAM:
            initial[a] = r.val(f'sysbus ReadDoubleWord 0x{a:08X}')
        print(f"  initial RAM: " + " ".join(f"{n}=0x{initial[a]:08X}" for n, a in WATCH_RAM))

        # Set PC + cleared GPRs (so we know what's "zero" vs "boot has run")
        r.cmd(f'sysbus.cpu PC 0x{ENTRY_PC:08X}', wait=0.2)
        r.cmd('sysbus.cpu LR 0xDEADBEEF', wait=0.1)
        r.cmd('sysbus.cpu SetRegisterUnsafe 1 0x4000FF00', wait=0.1)
        pc = r.val('sysbus.cpu PC')
        print(f"  entry: PC=0x{pc:08X}, SP=0x4000FF00, LR=0xDEADBEEF")

        # Step in a tight loop, sample every 100 steps
        print("\n=== Running ===")
        BUDGET = 200_000
        SAMPLE_EVERY = 500
        t0 = time.time()
        steps = 0
        pages = Counter()
        last_pc = pc
        stuck = 0
        last_change_step = 0

        while steps < BUDGET:
            # Send a burst of Step commands without waiting (TCP queue)
            for _ in range(SAMPLE_EVERY):
                r.s.sendall(b"sysbus.cpu Step\n")
            # Now drain the responses
            r.s.settimeout(5)
            time.sleep(SAMPLE_EVERY * 0.005)
            buf = b""
            for _ in range(20):
                try:
                    chunk = r.s.recv(65536)
                    if not chunk: break
                    buf += chunk
                    if buf.count(b"(ecm)") >= SAMPLE_EVERY:
                        break
                except socket.timeout:
                    break
            steps += SAMPLE_EVERY

            pc = r.val('sysbus.cpu PC')
            if pc is None:
                print(f"  step {steps}: no PC response")
                break

            # Categorize PC
            if pc < 0x10000: cat = "boot"
            elif pc < 0x1C0000: cat = "OS"
            elif pc < 0x200000: cat = "cal"
            elif 0x003F8000 <= pc < 0x0041A000: cat = "ramA2L"
            elif 0x40000000 <= pc < 0x40020000: cat = "SRAM"
            elif 0xC3F00000 <= pc <= 0xC3FFFFFF: cat = "pbridgeA"
            elif 0xFFE00000 <= pc <= 0xFFFFFFFF: cat = "pbridgeB"
            else: cat = "other"
            pages[cat] += 1

            # Stuck check
            if pc == last_pc:
                stuck += 1
            else:
                stuck = 0
                last_change_step = steps
            last_pc = pc

            # Poll watched RAM for any change
            changes = []
            for n, a in WATCH_RAM:
                cur = r.val(f'sysbus ReadDoubleWord 0x{a:08X}')
                if cur != initial[a]:
                    changes.append((n, a, initial[a], cur))
                    initial[a] = cur

            if changes:
                for n, a, old, cur in changes:
                    print(f"  *** step {steps}: {n} @0x{a:08X} 0x{old:08X} -> 0x{cur:08X} ***")

            elapsed = time.time() - t0
            ips = steps / max(elapsed, 0.01)
            if steps % 5000 == 0 or stuck > 5:
                print(f"  step {steps:>7d}: PC=0x{pc:08X} ({cat})  {ips:.0f} ips  "
                      f"last_change={last_change_step}")

            if stuck > 20:
                print(f"  CPU stuck at PC=0x{pc:08X} for >10000 steps - stopping")
                break

        print(f"\n=== Summary after {steps} steps in {time.time()-t0:.1f}s ===")
        print(f"  PC-page histogram (sampled):")
        for cat, count in pages.most_common():
            print(f"    {cat:12s}  {count}")
        print(f"\n  Final RAM state:")
        for n, a in WATCH_RAM:
            v = r.val(f'sysbus ReadDoubleWord 0x{a:08X}')
            mark = ""
            if a == 0x003F8614 and v not in (0, 0xFFFFFFFF):
                mark = "  <-- KEY LIKELY POPULATED!"
            print(f"    {n:14s} @0x{a:08X} = 0x{v:08X}{mark}")
        r2 = r.val('sysbus.cpu GetRegisterUnsafe 2')
        r13 = r.val('sysbus.cpu GetRegisterUnsafe 13')
        final_pc = r.val('sysbus.cpu PC')
        print(f"\n  Final PC=0x{final_pc:08X}  r2=0x{r2:08X}  r13=0x{r13:08X}")

        r.close()
    finally:
        proc.terminate()
        try: proc.wait(timeout=5)
        except subprocess.TimeoutExpired: proc.kill()


if __name__ == "__main__":
    main()
