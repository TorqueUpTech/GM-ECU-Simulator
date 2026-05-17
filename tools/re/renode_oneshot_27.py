"""Renode one-shot: skip boot, inject a fake $27 01 message at
DAT_003fe908, call FUN_0000698C (the boot dispatcher), trace the
execution path until the function returns.

Goal: verify Renode can run real e200z6 firmware end-to-end and
confirm whether the dispatcher path touches the security key
flash address (0x0000C134) or RAM key location (0x003F8614).

Expected from static analysis:
    - FUN_0000698C sees SID 0x27, sets state flags, calls FUN_00006114
    - FUN_00006114 writes the bypass response (67 01 00 00) to
      DAT_003fe9ac onwards, then calls FUN_00004a80 (CAN tx)
    - No access to 0x0000C134, no access to 0x003F8614

If Renode shows different behavior, that's interesting. If it
matches static analysis, we have an independent verification.
"""

from __future__ import annotations
import socket, time, sys, re, subprocess
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

PORT          = 12349
BIN_PATH      = "c:/Tools/Renode/bins/e38.bin"
PLATFORM_REPL = "c:/Tools/Renode/mpc5566_e38.repl"
RENODE_EXE    = r"C:\Tools\Renode\renode_1.16.1-dotnet_portable\renode.exe"

# Bin/key addresses we care about
ADDR_KEY_FLASH   = 0x0000C134
ADDR_SEED_FLASH  = 0x0000C13C
ADDR_KEY_RAM     = 0x003F8614
ADDR_SEED_RAM    = 0x003F861C
ADDR_LOCK_RAM    = 0x00403990
ADDR_MSG_BUF     = 0x003FE908   # where dispatcher reads param_1
ADDR_RESP_BUF    = 0x003FE9AC   # where boot $27 writes its response

HEX_RE = re.compile(r"^0x[0-9A-Fa-f]+$")


class RenodeClient:
    def __init__(self, port):
        self.s = socket.create_connection(("127.0.0.1", port), timeout=10)
        self.s.settimeout(2.5)
        try:
            self.s.recv(8192)  # drain banner
        except socket.timeout:
            pass

    def cmd(self, c, wait=0.3):
        self.s.sendall((c + "\n").encode())
        time.sleep(wait)
        out = []
        try:
            while True:
                ch = self.s.recv(16384).decode(errors="replace")
                if not ch:
                    break
                out.append(ch)
                if ch.rstrip().endswith(")"):
                    break
        except socket.timeout:
            pass
        return "".join(out)

    def val(self, c):
        raw = self.cmd(c)
        for line in raw.splitlines():
            line = line.strip()
            if HEX_RE.match(line):
                return int(line, 16)
        return None

    def close(self):
        self.s.close()


def main():
    # Spin up Renode
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

        # Verify bin at FUN_0000698C
        first = r.val('sysbus ReadDoubleWord 0x0000698C')
        print(f"  first word of FUN_0000698C: 0x{first:08X}")

        # ---- prime the message buffer at DAT_003fe908 with a fake $27 01 ----
        # Per the ISO-TP unpacker: [00, 02, SID=0x27, sub=0x01, ...]
        print("\n=== Inject fake $27 01 message at 0x003FE908 ===")
        msg = [0x00, 0x02, 0x27, 0x01, 0x00, 0x00, 0x00, 0x00]
        for i, b in enumerate(msg):
            r.cmd(f'sysbus WriteByte 0x{ADDR_MSG_BUF + i:08X} 0x{b:02X}')
        # Read back to verify
        for i in range(8):
            v = r.val(f'sysbus ReadByte 0x{ADDR_MSG_BUF + i:08X}')
            print(f"  [0x{ADDR_MSG_BUF + i:08X}] = 0x{v:02X}")

        # ---- skip boot: set SDA bases + stack + LR trap + arg + PC ----
        print("\n=== Skip boot: poke register state ===")
        r.cmd('sysbus.cpu SetRegisterUnsafe 1 0x4000FF00')     # SP
        r.cmd('sysbus.cpu SetRegisterUnsafe 2 0x0001CED0')     # SDA2 base
        r.cmd('sysbus.cpu SetRegisterUnsafe 3 0x003FE908')     # r3 = param_1 (msg ptr)
        r.cmd('sysbus.cpu SetRegisterUnsafe 13 0x00400000')    # SDA base
        r.cmd('sysbus.cpu LR 0xDEADBEEF')                       # return-trap
        r.cmd('sysbus.cpu PC 0x0000698C')                       # boot dispatcher
        pc = r.val('sysbus.cpu PC')
        sp = r.val('sysbus.cpu GetRegisterUnsafe 1')
        r3 = r.val('sysbus.cpu GetRegisterUnsafe 3')
        print(f"  PC=0x{pc:08X}  SP=0x{sp:08X}  r3(msg)=0x{r3:08X}  LR=0xDEADBEEF")

        # Snapshot the watched flash/RAM cells before stepping
        print("\n=== Pre-step state of watched addresses ===")
        for name, addr in [
            ("key (flash)",  ADDR_KEY_FLASH),
            ("seed (flash)", ADDR_SEED_FLASH),
            ("key (RAM)",    ADDR_KEY_RAM),
            ("seed (RAM)",   ADDR_SEED_RAM),
            ("lock (RAM)",   ADDR_LOCK_RAM),
            ("resp[0..3]",   ADDR_RESP_BUF),
        ]:
            w = r.val(f'sysbus ReadDoubleWord 0x{addr:08X}')
            print(f"  {name:14s} @0x{addr:08X} = 0x{w:08X}")

        # ---- run until LR-trap or budget ----
        print("\n=== Step the dispatcher to completion ===")
        BUDGET = 50_000
        STEP_CHUNK = 500
        # Renode's "Step <N>" runs N instructions at once
        steps_done = 0
        last_pc = pc
        while steps_done < BUDGET:
            r.cmd(f'sysbus.cpu Step {STEP_CHUNK}', wait=0.5)
            steps_done += STEP_CHUNK
            cur_pc = r.val('sysbus.cpu PC')
            if cur_pc is None:
                print(f"  [stalled - no PC read at step {steps_done}]")
                break
            print(f"  after {steps_done:>6d} steps: PC=0x{cur_pc:08X}")
            if cur_pc == 0xDEADBEEC or cur_pc == 0xDEADBEEF:
                print("  >> hit LR-trap, function returned")
                break
            if cur_pc == last_pc:
                print("  >> PC stuck (tight loop) - stopping")
                break
            last_pc = cur_pc

        # ---- post-state ----
        print("\n=== Post-step state ===")
        for name, addr in [
            ("key (flash)",  ADDR_KEY_FLASH),
            ("seed (flash)", ADDR_SEED_FLASH),
            ("key (RAM)",    ADDR_KEY_RAM),
            ("seed (RAM)",   ADDR_SEED_RAM),
            ("lock (RAM)",   ADDR_LOCK_RAM),
            ("resp[0..3]",   ADDR_RESP_BUF),
            ("resp[4..7]",   ADDR_RESP_BUF + 4),
        ]:
            w = r.val(f'sysbus ReadDoubleWord 0x{addr:08X}')
            print(f"  {name:14s} @0x{addr:08X} = 0x{w:08X}")

        # Read the bytes the boot $27 sets up as a response
        resp_bytes = []
        for i in range(8):
            v = r.val(f'sysbus ReadByte 0x{ADDR_RESP_BUF + i:08X}')
            resp_bytes.append(v)
        print(f"\n  Response bytes at 0x{ADDR_RESP_BUF:08X}: " +
              " ".join(f"{b:02X}" for b in resp_bytes))
        print(f"  (expected from static analysis: 00 04 67 01 00 00 ?? ??)")

        r.close()
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            proc.kill()


if __name__ == "__main__":
    main()
