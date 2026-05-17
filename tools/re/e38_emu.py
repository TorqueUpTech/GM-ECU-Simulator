"""
e38_emu.py - Unicorn-based PowerPC emulator harness for the E38 bin.

Goals:
    1. Load the 2 MiB flash bin.
    2. Stand up the address spaces we know about (flash, RAM regions both
       at the A2L's logical addresses and at the MPC5566 physical SRAM
       region at 0x40000000).
    3. Provide helpers to call specific functions with controlled register
       state, traps to stop on return.
    4. Hook memory reads / writes so we can watch for accesses to the
       security-key flash address (0x0000C134) and RAM (0x003F8614 etc.).

This is NOT a faithful MPC5566 emulator - we ignore peripherals, MMU
configuration, interrupts, and timing. The goal is to let arbitrary
code-flow paths through user code execute long enough to reveal which
addresses get touched.
"""

from __future__ import annotations

import sys
from pathlib import Path
from typing import Callable

from unicorn import (
    Uc, UC_ARCH_PPC, UC_MODE_PPC32, UC_MODE_BIG_ENDIAN,
    UC_HOOK_MEM_READ, UC_HOOK_MEM_WRITE, UC_HOOK_CODE,
    UC_HOOK_MEM_UNMAPPED, UC_HOOK_MEM_READ_UNMAPPED,
    UC_HOOK_MEM_WRITE_UNMAPPED, UC_HOOK_MEM_FETCH_UNMAPPED,
    UcError,
    UC_PROT_READ, UC_PROT_WRITE, UC_PROT_EXEC, UC_PROT_ALL,
)
from unicorn.ppc_const import (
    UC_PPC_REG_PC,
    UC_PPC_REG_LR,
    UC_PPC_REG_1, UC_PPC_REG_2, UC_PPC_REG_3, UC_PPC_REG_4,
    UC_PPC_REG_5, UC_PPC_REG_6, UC_PPC_REG_7, UC_PPC_REG_8,
    UC_PPC_REG_9, UC_PPC_REG_10, UC_PPC_REG_11, UC_PPC_REG_12,
    UC_PPC_REG_13,
)


# ---------------------------------------------------------------------------
# Memory layout (chosen to match what we determined from static analysis).
# ---------------------------------------------------------------------------
BIN_PATH        = (
    r"C:\Users\Nathan\OneDrive\Cars\VF2 HSV R8 LSA\GM Programming"
    r"\PCMHacking\Bins\GM Global A\smokeshow_12647991.bin"
)
FLASH_BASE      = 0x00000000
FLASH_SIZE      = 0x00200000   # 2 MiB
RAM_A2L_BASE    = 0x003F8000   # A2L logical RAM start
RAM_A2L_SIZE    = 0x00022000   # covers RAM_A2L_5, GAP, and RAM_A2L_6
RAM_PHYS_BASE   = 0x40000000   # MPC5566 on-chip SRAM
RAM_PHYS_SIZE   = 0x00020000   # 128 KiB

# Magic "return" address we set LR to so the emulator stops cleanly when
# the function under test executes blr.
TRAP_RET        = 0xDEADBEEF


# ---------------------------------------------------------------------------
# Convenience map: register name -> Unicorn register id.
# ---------------------------------------------------------------------------
REGS = {
    "pc":  UC_PPC_REG_PC,
    "lr":  UC_PPC_REG_LR,
    "r1":  UC_PPC_REG_1,
    "r2":  UC_PPC_REG_2,
    "r3":  UC_PPC_REG_3,
    "r4":  UC_PPC_REG_4,
    "r5":  UC_PPC_REG_5,
    "r6":  UC_PPC_REG_6,
    "r7":  UC_PPC_REG_7,
    "r8":  UC_PPC_REG_8,
    "r9":  UC_PPC_REG_9,
    "r10": UC_PPC_REG_10,
    "r11": UC_PPC_REG_11,
    "r12": UC_PPC_REG_12,
    "r13": UC_PPC_REG_13,
}


class E38Emu:
    """Minimal Unicorn harness wired for the E38 12647991 bin."""

    def __init__(self, bin_path: str = BIN_PATH, verbose: bool = False):
        self.uc = Uc(UC_ARCH_PPC, UC_MODE_PPC32 | UC_MODE_BIG_ENDIAN)
        self.verbose = verbose
        self._read_log: list[tuple[int, int, int]] = []   # (pc, addr, size)
        self._write_log: list[tuple[int, int, int, int]] = []  # (pc, addr, size, value)
        self._instr_count = 0
        self._max_instrs = 1_000_000
        self._stop_reason: str | None = None

        # Map memory regions.
        self.uc.mem_map(FLASH_BASE, FLASH_SIZE, UC_PROT_READ | UC_PROT_EXEC)
        self.uc.mem_map(RAM_A2L_BASE, RAM_A2L_SIZE, UC_PROT_ALL)
        self.uc.mem_map(RAM_PHYS_BASE, RAM_PHYS_SIZE, UC_PROT_ALL)

        # Load the bin into flash.
        data = Path(bin_path).read_bytes()
        if len(data) != FLASH_SIZE:
            raise RuntimeError(
                f"unexpected bin size {len(data)} (expected {FLASH_SIZE})")
        self.uc.mem_write(FLASH_BASE, data)

        # Hook everything we care about.
        self.uc.hook_add(UC_HOOK_MEM_READ,  self._on_read)
        self.uc.hook_add(UC_HOOK_MEM_WRITE, self._on_write)
        self.uc.hook_add(UC_HOOK_CODE,      self._on_code)
        self.uc.hook_add(UC_HOOK_MEM_UNMAPPED, self._on_unmapped)

    # ---- register helpers ------------------------------------------------
    def set_reg(self, name: str, val: int) -> None:
        self.uc.reg_write(REGS[name], val & 0xFFFFFFFF)

    def get_reg(self, name: str) -> int:
        return self.uc.reg_read(REGS[name]) & 0xFFFFFFFF

    def dump_regs(self, names: list[str] | None = None) -> str:
        if names is None:
            names = ["pc", "lr", "r1", "r2", "r3", "r4", "r5", "r13"]
        return "  ".join(f"{n}=0x{self.get_reg(n):08X}" for n in names)

    # ---- hook callbacks --------------------------------------------------
    def _on_read(self, uc, access, addr, size, value, user):
        pc = uc.reg_read(UC_PPC_REG_PC)
        self._read_log.append((pc, addr, size))
        if self.verbose:
            try:
                v = int.from_bytes(uc.mem_read(addr, size), "big")
                print(f"    [R] @0x{pc:08X}  read{size}  0x{addr:08X} = 0x{v:0{size*2}X}")
            except Exception:
                pass

    def _on_write(self, uc, access, addr, size, value, user):
        pc = uc.reg_read(UC_PPC_REG_PC)
        self._write_log.append((pc, addr, size, value))
        if self.verbose:
            print(f"    [W] @0x{pc:08X}  write{size} 0x{addr:08X} = 0x{value & ((1<<(size*8))-1):0{size*2}X}")

    def _on_code(self, uc, addr, size, user):
        self._instr_count += 1
        if self._instr_count > self._max_instrs:
            self._stop_reason = "instruction budget exhausted"
            uc.emu_stop()
            return
        if addr == TRAP_RET:
            self._stop_reason = "hit TRAP_RET (function returned)"
            uc.emu_stop()
            return

    def _on_unmapped(self, uc, access, addr, size, value, user):
        pc = uc.reg_read(UC_PPC_REG_PC)
        self._stop_reason = (
            f"unmapped {'read' if access in (16, 19) else 'write/fetch'} at "
            f"0x{addr:08X} (size {size}) from pc=0x{pc:08X}"
        )
        return False   # signal Unicorn that the access wasn't handled

    # ---- run helpers -----------------------------------------------------
    def call_function(self, entry: int, args: list[int] | None = None,
                      max_instrs: int = 1_000_000) -> str:
        """Set up registers per PPC EABI calling convention, jump to entry,
        run until LR-trap or a stop condition. Returns the stop reason."""
        args = args or []
        # Argument regs r3..r10
        arg_regs = ["r3", "r4", "r5", "r6", "r7", "r8", "r9", "r10"]
        for r in arg_regs:
            self.set_reg(r, 0)
        for i, a in enumerate(args[:8]):
            self.set_reg(arg_regs[i], a)
        # Default SDA / stack pointers (skipping early boot).
        self.set_reg("r13", 0x00400000)
        self.set_reg("r2",  0x0001CED0)
        self.set_reg("r1",  RAM_A2L_BASE + RAM_A2L_SIZE - 0x100)  # near top of RAM
        self.set_reg("lr",  TRAP_RET)

        self._instr_count = 0
        self._max_instrs = max_instrs
        self._stop_reason = None
        self._read_log.clear()
        self._write_log.clear()

        try:
            self.uc.emu_start(entry, TRAP_RET, count=max_instrs)
        except UcError as e:
            if self._stop_reason is None:
                self._stop_reason = f"UcError: {e}"
        return self._stop_reason or "max instructions reached"

    @property
    def reads(self):  return list(self._read_log)
    @property
    def writes(self): return list(self._write_log)
    @property
    def instrs(self): return self._instr_count


# ---------------------------------------------------------------------------
# Self-test: call the SDA-init function and confirm we get expected results.
# ---------------------------------------------------------------------------
def selftest() -> int:
    print("[selftest] Loading bin and running SDA init at 0x000333D8...")
    emu = E38Emu()
    stop = emu.call_function(0x000333D8, args=[0])
    r2 = emu.get_reg("r2")
    r13 = emu.get_reg("r13")
    print(f"  stop reason: {stop}")
    print(f"  instructions: {emu.instrs}")
    print(f"  r2  = 0x{r2:08X}  (expected 0x0001CED0)")
    print(f"  r13 = 0x{r13:08X}  (expected 0x00400000)")
    ok = r2 == 0x0001CED0 and r13 == 0x00400000
    print(f"  result: {'OK' if ok else 'FAIL'}")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(selftest())
