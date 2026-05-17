"""Renode + GDB watchpoint hunt (v2 - fixed protocol sync).

Strategy:
    1. Start Renode with the MPC5566/E38 platform, load the bin.
    2. Set PC to FUN_00014EE0 (OS init) and configure register state.
    3. Start Renode's GDB server.
    4. Connect with a minimal GDB-remote-protocol client.
    5. Drain any unsolicited stop reply (Renode sends one on connect).
    6. Set write watchpoints on BaOSAF_SecurKey, BaOSAF_SecurSeed,
       SbOSAF_SecurAccessLocked, and a read watchpoint on flash 0xC134.
    7. Send "continue". CPU runs at native speed until a watchpoint
       fires or a timeout.
"""

from __future__ import annotations
import socket, time, sys, subprocess
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

MONITOR_PORT = 12362
GDB_PORT     = 3335
RENODE_EXE   = r"C:\Tools\Renode\renode_1.16.1-dotnet_portable\renode.exe"
PLATFORM     = "c:/Tools/Renode/mpc5566_e38_full.repl"
BIN_PATH     = "c:/Tools/Renode/bins/e38.bin"

ADDR_KEY_RAM   = 0x003F8614
ADDR_SEED_RAM  = 0x003F861C
ADDR_LOCK_RAM  = 0x00403990
ADDR_KEY_FLASH = 0x0000C134
ENTRY_PC       = 0x00014EE0

# Renode PowerPC GDB layout (matches standard PowerPC GDB target):
#   reg 64 = PC, reg 65 = MSR, reg 66 = CR, reg 67 = LR, reg 68 = CTR.
# Earlier confusion: read_register formats regno as hex, so 'p40' in the
# wire format is reg 0x40 = 64.
REG_PC = 64
REG_LR = 67


def gdb_checksum(payload: bytes) -> bytes:
    s = sum(payload) % 256
    return f"{s:02x}".encode()


class GdbClient:
    def __init__(self, host, port, debug=False):
        self.s = socket.create_connection((host, port), timeout=10)
        self.s.settimeout(2)
        self.debug = debug
        self._buf = b""

    def _read_more(self):
        try:
            data = self.s.recv(16384)
            self._buf += data
            return len(data)
        except socket.timeout:
            return 0

    def _next_packet(self, timeout=3) -> bytes | None:
        """Read one '$...#cc' packet from the buffer; refill from socket
        as needed. Returns payload (no framing) or None on timeout."""
        deadline = time.time() + timeout
        while True:
            # Skip ACK bytes / stray chars until we hit '$'
            while self._buf and self._buf[:1] in (b"+", b"-"):
                self._buf = self._buf[1:]
            i = self._buf.find(b"$")
            if i >= 0:
                # Find '#'
                j = self._buf.find(b"#", i + 1)
                if j >= 0 and len(self._buf) >= j + 3:
                    payload = self._buf[i + 1:j]
                    self._buf = self._buf[j + 3:]
                    # Send our ACK
                    self.s.sendall(b"+")
                    if self.debug: print(f"  recv: {payload!r}")
                    return payload
            if time.time() > deadline:
                return None
            if self._read_more() == 0:
                # No data available right now; retry until deadline
                time.sleep(0.01)

    def send(self, payload: bytes):
        pkt = b"$" + payload + b"#" + gdb_checksum(payload)
        if self.debug: print(f"  send: {payload!r}")
        self.s.sendall(pkt)

    def cmd(self, payload: bytes, timeout=3) -> bytes | None:
        self.send(payload)
        return self._next_packet(timeout=timeout)

    def drain_initial(self):
        """Renode sends an unsolicited stop reply on connect. Consume it."""
        time.sleep(0.2)
        self._read_more()
        # Try to read a packet
        pkt = self._next_packet(timeout=1)
        return pkt

    def read_memory(self, addr, length):
        resp = self.cmd(f"m{addr:x},{length:x}".encode())
        if resp is None or resp.startswith(b"E"):
            return None
        try: return bytes.fromhex(resp.decode())
        except Exception: return None

    def read_register(self, regno):
        resp = self.cmd(f"p{regno:x}".encode())
        if resp is None or resp.startswith(b"E") or resp == b"":
            return None
        try: return int(resp.decode(), 16)
        except Exception: return None

    def set_watchpoint(self, kind, addr, length):
        return self.cmd(f"Z{kind},{addr:x},{length:x}".encode())

    def cont(self, timeout=120):
        self.send(b"vCont;c")
        return self._next_packet(timeout=timeout)

    def halt(self):
        """Send 0x03 (Ctrl-C) to halt a running CPU. Returns the stop reply."""
        self.s.sendall(b"\x03")
        return self._next_packet(timeout=3)

    def close(self):
        try: self.s.close()
        except Exception: pass


def main():
    print("[+] Spawning Renode...")
    proc = subprocess.Popen(
        [RENODE_EXE, "--plain", "--disable-gui", "--hide-monitor",
         "--hide-log", "--port", str(MONITOR_PORT)],
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
    )
    time.sleep(7)
    try:
        m = socket.create_connection(("127.0.0.1", MONITOR_PORT), timeout=10)
        m.settimeout(2)
        try: m.recv(4096)
        except: pass
        for c in [
            'mach create "ecm"',
            f'machine LoadPlatformDescription @{PLATFORM}',
            f'sysbus LoadBinary @{BIN_PATH} 0x00000000',
            f'sysbus.cpu PC 0x{ENTRY_PC:08X}',
            'sysbus.cpu LR 0xDEADBEEF',
            'sysbus.cpu SetRegisterUnsafe 1 0x4000FF00',
            f'machine StartGdbServer {GDB_PORT}',
        ]:
            m.sendall((c + "\n").encode())
            time.sleep(0.6)
            try:
                while True:
                    ch = m.recv(8192).decode(errors="replace")
                    if not ch or ch.rstrip().endswith(")"): break
            except socket.timeout: pass
        m.close()

        print(f"\n[+] Connecting to GDB server on port {GDB_PORT}...")
        g = GdbClient("127.0.0.1", GDB_PORT, debug=True)

        # Drain the initial stop reply Renode sends on connect.
        init = g.drain_initial()
        print(f"  initial packet: {init!r}")

        # qSupported handshake
        sup = g.cmd(b"qSupported")
        print(f"  qSupported: {sup[:120] if sup else None}")

        # Try a few register reads
        for cand in (41, 42, 43, 44, 64, 65, 67):
            v = g.read_register(cand)
            print(f"  p{cand:x} = {v if v is None else f'0x{v:08X}'}")

        # Read PC + LR via specific register numbers we already verified
        pc = g.read_register(REG_PC)
        lr = g.read_register(REG_LR)
        print(f"\n[+] PC={pc and hex(pc)}  LR={lr and hex(lr)}  (expected: PC=0x{ENTRY_PC:08X} LR=0xDEADBEEF)")

        mem = g.read_memory(ENTRY_PC, 16)
        print(f"  flash @0x{ENTRY_PC:08X}: {mem.hex(' ') if mem else 'ERR'}")
        mem_c134 = g.read_memory(ADDR_KEY_FLASH, 4)
        print(f"  flash @0x{ADDR_KEY_FLASH:08X} (security key): {mem_c134.hex(' ') if mem_c134 else 'ERR'}")
        mem_key_ram = g.read_memory(ADDR_KEY_RAM, 4)
        print(f"  RAM   @0x{ADDR_KEY_RAM:08X} (BaOSAF_SecurKey): {mem_key_ram.hex(' ') if mem_key_ram else 'ERR'}")

        # Install watchpoints
        print("\n[+] Installing watchpoints...")
        for kind, addr, length, name in [
            (2, ADDR_KEY_RAM,   2, "Z2 write BaOSAF_SecurKey"),
            (2, ADDR_SEED_RAM,  2, "Z2 write BaOSAF_SecurSeed"),
            (2, ADDR_LOCK_RAM,  1, "Z2 write SbOSAF_SecurAccessLocked"),
            (3, ADDR_KEY_FLASH, 2, "Z3 read flash key 0xC134"),
        ]:
            resp = g.set_watchpoint(kind, addr, length)
            print(f"  {name}: {resp!r}")

        # Continue execution for a fixed wall-clock time, then halt and
        # see where the CPU is.
        RUN_SECONDS = 30
        print(f"\n[+] vCont;c then halt after {RUN_SECONDS}s of native execution...")
        g.debug = False    # quiet during the run
        t0 = time.time()
        g.send(b"vCont;c")
        # Wait either for a watchpoint hit OR for our wall-clock budget.
        stop = g._next_packet(timeout=RUN_SECONDS)
        elapsed = time.time() - t0
        if stop is not None:
            print(f"  >> Stopped at {elapsed:.2f}s with: {stop!r}")
        else:
            print(f"  >> No watchpoint hit in {RUN_SECONDS}s - sending halt")
            stop = g.halt()
            print(f"  halt reply: {stop!r}")
        g.debug = True

        pc = g.read_register(REG_PC)
        lr = g.read_register(REG_LR)
        print(f"\n[+] After run: PC={pc and hex(pc)}  LR={lr and hex(lr)}")

        # Dump a wide swath of OSAF RAM (0x003F8580 .. 0x003F86C0) to see
        # if ANY byte in the security cluster changed.
        print("\n[+] OSAF security-cluster RAM dump (0x003F8580..0x003F86C0):")
        start = 0x003F8580
        for off in range(0, 0x140, 16):
            v = g.read_memory(start + off, 16)
            if v is None:
                print(f"  0x{start+off:08X}  ERR")
                continue
            non_zero = any(b != 0 for b in v)
            marker = "  <-- non-zero" if non_zero else ""
            print(f"  0x{start+off:08X}  {v.hex(' ')}{marker}")

        for n, a in [("KEY_RAM", ADDR_KEY_RAM), ("SEED_RAM", ADDR_SEED_RAM),
                     ("LOCK_RAM", ADDR_LOCK_RAM), ("KEY_FLASH", ADDR_KEY_FLASH)]:
            v = g.read_memory(a, 4)
            mark = " <-- CHANGED" if (a in (ADDR_KEY_RAM, ADDR_SEED_RAM, ADDR_LOCK_RAM)
                                       and v and any(b != 0 for b in v)) else ""
            print(f"  {n} @0x{a:08X} = {v.hex(' ') if v else 'ERR'}{mark}")

        g.close()
    finally:
        proc.terminate()
        try: proc.wait(timeout=5)
        except subprocess.TimeoutExpired: proc.kill()


if __name__ == "__main__":
    main()
