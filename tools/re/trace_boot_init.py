"""Emulate the boot init function (FUN_00014ee0) and log every access
to addresses that could be related to the security key copy.

What we're hunting:
    - Any READ of flash 0x0000C134 (security key source) or 0x0000C13C
      (algo92(key) source)
    - Any WRITE to RAM 0x003F861C (BaOSAF_SecurSeed) or 0x003F8614
      (BaOSAF_SecurKey)
    - Any READ of the broader boot metadata block 0x0000C000-0x0000C200
    - Any WRITE to the broader OSAF state region 0x003F8580-0x003F86C0

If the boot init copies the key/seed to RAM, we'll see the smoking gun.
"""

from __future__ import annotations
import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))

from unicorn import (
    UC_HOOK_MEM_UNMAPPED, UC_PROT_ALL,
)
from e38_emu import E38Emu

WATCH = [
    # Boot metadata block (flash) - reads here reveal key/seed source loads
    (0x0000C000, 0x0000C400, "BOOT_META"),
    # OSAF state RAM (the broader cluster)
    (0x003F8580, 0x003F8700, "OSAF_RAM"),
    (0x003FE640, 0x003FE680, "OSAF_STATE"),
    (0x00403980, 0x004039A0, "OSAF_LOCK"),
]


def region_for(addr):
    for lo, hi, name in WATCH:
        if lo <= addr < hi:
            return name
    return None


class TraceEmu(E38Emu):
    def __init__(self):
        super().__init__()
        self.uc.hook_add(UC_HOOK_MEM_UNMAPPED, self._on_unmapped_fixup)
        self._mapped_pages = set()
        self._watch_log = []

    def _on_unmapped_fixup(self, uc, access, addr, size, value, user):
        page = addr & ~0xFFF
        if page in self._mapped_pages:
            return False
        try:
            uc.mem_map(page, 0x1000, UC_PROT_ALL)
            self._mapped_pages.add(page)
            return True
        except Exception:
            return False

    def _on_read(self, uc, access, addr, size, value, user):
        super()._on_read(uc, access, addr, size, value, user)
        tag = region_for(addr)
        if tag:
            pc = self.get_reg("pc")
            try:
                v = int.from_bytes(uc.mem_read(addr, size), "big")
                msg = f"R {tag:12s} pc=0x{pc:08X} 0x{addr:08X}[{size}] = 0x{v:0{size*2}X}"
                self._watch_log.append(msg)
                print(f"  {msg}")
            except Exception:
                pass

    def _on_write(self, uc, access, addr, size, value, user):
        super()._on_write(uc, access, addr, size, value, user)
        tag = region_for(addr)
        if tag:
            pc = self.get_reg("pc")
            v = value & ((1 << (size * 8)) - 1)
            msg = f"W {tag:12s} pc=0x{pc:08X} 0x{addr:08X}[{size}] = 0x{v:0{size*2}X}"
            self._watch_log.append(msg)
            print(f"  {msg}")


def main():
    print("[trace] Emulating FUN_00014ee0 (boot init)...")
    emu = TraceEmu()
    reason = emu.call_function(0x00014EE0, max_instrs=500_000)
    print(f"\nStop reason: {reason}")
    print(f"Instructions: {emu.instrs}")
    print(f"Total reads:  {len(emu.reads)}")
    print(f"Total writes: {len(emu.writes)}")
    print(f"On-demand mapped pages: {len(emu._mapped_pages)}")
    print(f"Watch-range accesses: {len(emu._watch_log)}")
    if not emu._watch_log:
        print("  (none - nothing touched the boot metadata or OSAF regions)")


if __name__ == "__main__":
    main()
