"""Run the boot's $27 handler (FUN_00006114) under emulation and log
every memory access in the OSAF-relevant ranges + the boot metadata
block. If the handler reads the security key value at runtime, the
trace will show the read from 0x0000C134.

Stubs out any peripheral / unmapped access by mapping pages on demand.
"""

from __future__ import annotations
import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))

from unicorn import (
    UC_HOOK_MEM_UNMAPPED, UC_PROT_ALL, UC_PROT_READ, UC_PROT_EXEC,
)

from e38_emu import (
    E38Emu, FLASH_BASE, FLASH_SIZE, TRAP_RET,
)

# Address regions we care about - reads/writes here get logged loudly.
WATCH_RANGES = [
    (0x0000C000, 0x0000C200, "BOOT_METADATA"),
    (0x003F8580, 0x003F8700, "OSAF_DATA"),
    (0x003FE640, 0x003FE680, "OSAF_STATE"),
    (0x00403980, 0x004039A0, "OSAF_LOCK"),
]


def region_for(addr: int):
    for lo, hi, name in WATCH_RANGES:
        if lo <= addr < hi:
            return name
    return None


class TracingEmu(E38Emu):
    def __init__(self, **kw):
        super().__init__(**kw)
        # Map "everything else" as RAM on demand so MMIO accesses don't crash.
        self.uc.hook_add(UC_HOOK_MEM_UNMAPPED, self._on_unmapped_fixup)
        self._mapped_pages: set[int] = set()

    def _on_unmapped_fixup(self, uc, access, addr, size, value, user):
        page = addr & ~0xFFF
        if page in self._mapped_pages:
            return False
        try:
            uc.mem_map(page, 0x1000, UC_PROT_ALL)
            self._mapped_pages.add(page)
            return True   # access can now succeed; resume
        except Exception:
            return False

    # Override read/write logging to mark watched regions.
    def _on_read(self, uc, access, addr, size, value, user):
        super()._on_read(uc, access, addr, size, value, user)
        tag = region_for(addr)
        if tag:
            pc = self.get_reg("pc")
            try:
                v = int.from_bytes(uc.mem_read(addr, size), "big")
                print(f"  [{tag}:R] pc=0x{pc:08X} read{size}  0x{addr:08X} = 0x{v:0{size*2}X}")
            except Exception:
                pass

    def _on_write(self, uc, access, addr, size, value, user):
        super()._on_write(uc, access, addr, size, value, user)
        tag = region_for(addr)
        if tag:
            pc = self.get_reg("pc")
            print(f"  [{tag}:W] pc=0x{pc:08X} write{size} 0x{addr:08X} = 0x{value & ((1<<(size*8))-1):0{size*2}X}")


def main():
    print("[trace] Emulating FUN_00006114 (boot $27 handler)...")
    emu = TracingEmu()
    reason = emu.call_function(0x00006114, max_instrs=5000)
    print(f"\nStop reason: {reason}")
    print(f"Instructions executed: {emu.instrs}")
    print(f"Total reads:  {len(emu.reads)}")
    print(f"Total writes: {len(emu.writes)}")
    print(f"Mapped-on-demand pages: {len(emu._mapped_pages)}")
    if emu._mapped_pages:
        print("  pages:", " ".join(f"0x{p:08X}" for p in sorted(emu._mapped_pages)[:10]))


if __name__ == "__main__":
    main()
