"""Statically extract the bootloader $27 SecurityAccess seed from a GM E38 (or
compatible) flash readback.

What this finds:

- The boot service dispatcher (a cmpwi/beq cascade over programming-session
  SIDs $1A/$20/$27/$28/$34/$36/$3E/$A2).
- The $27 trampoline reached via the dispatcher's `beq` for that SID.
- The $27 service handler called from the trampoline via `bl`.
- The six `li rT, imm` instructions inside the handler that build the response
  packet (status, length, response SID = $67, subfunction echo, seed-hi,
  seed-lo). The 5th and 6th immediates are reported as the seed, along with
  their flash offsets so the bytes can be patched if desired.

What this does NOT find: the OPERATIONAL $27 handler that performs the
algo-92 challenge-response. On observed E38 OS images that dispatcher is
table-driven (no cmpwi cascade), so a signature scanner cannot anchor on it.
A real PowerPC disassembler is needed for that path.

Reverse-engineering trace (E38, 2 MiB readback, derived manually from
`Bens_Stock Read_E38.bin` in this session):

  Boot dispatcher : 0x000069B0
  $27 cmpwi/beq   : 0x000069D4 / 0x000069D8  -> beq +0x48
  $27 trampoline  : 0x00006A20  -> bl -0x924
  $27 handler     : 0x00006114
  Response packet : 00 04 67 01 SEED_HI SEED_LO  (seed = 00 00 unconditionally)
  Seed offsets    : 0x00006156 (hi)  0x00006162 (lo)

Stdlib only. Usage:

  extract_e38_security_seed.py [-v] BIN [BIN ...]
"""

from __future__ import annotations

import argparse
import os
import sys
from dataclasses import dataclass


PROGRAMMING_SIDS = {0x1A, 0x20, 0x27, 0x28, 0x34, 0x36, 0x3E, 0xA2}
BOOT_REGION_END = 0x10000
DISPATCHER_WINDOW = 0x100

ALGO_92_CONSTANTS = (0x7D58, 0x8001)


def sext16(x: int) -> int:
    x &= 0xFFFF
    return x - 0x10000 if x & 0x8000 else x


def sext26(x: int) -> int:
    x &= 0x03FFFFFF
    return x - 0x04000000 if x & 0x02000000 else x


def read_u32_be(buf: bytes, off: int) -> int:
    return int.from_bytes(buf[off:off + 4], "big")


def is_cmpwi(word: int, sid: int | None = None) -> bool:
    if (word >> 26) != 11:
        return False
    bf = (word >> 23) & 0x7
    l = (word >> 21) & 0x1
    if bf != 0 or l != 0:
        return False
    if sid is not None and sext16(word & 0xFFFF) != sid:
        return False
    return True


def cmpwi_sid(word: int) -> int | None:
    if (word >> 26) != 11:
        return None
    if ((word >> 23) & 0x7) != 0 or ((word >> 21) & 0x1) != 0:
        return None
    simm = sext16(word & 0xFFFF)
    return simm if 0 <= simm <= 0xFF else None


def is_beq(word: int) -> bool:
    return (word >> 26) == 16 and ((word >> 21) & 0x1F) == 12 and ((word >> 16) & 0x1F) == 2


def beq_target(addr: int, word: int) -> int:
    bd = word & 0xFFFC
    if bd & 0x8000:
        bd -= 0x10000
    return addr + bd


def is_bl(word: int) -> bool:
    return (word >> 26) == 18 and (word & 1) == 1 and ((word >> 1) & 1) == 0


def bl_target(addr: int, word: int) -> int:
    li = sext26(word & 0x03FFFFFC)
    return addr + li


def is_li(word: int) -> int | None:
    """Return the destination register if this is `li rT, imm16` (addi rT, 0, imm); else None.

    Reports imm as the unsigned 16-bit value (matches how the bootloader uses li for
    small positive constants like 0x67).
    """
    if (word >> 26) != 14:
        return None
    ra = (word >> 16) & 0x1F
    if ra != 0:
        return None
    return (word >> 21) & 0x1F


def li_imm(word: int) -> int:
    return word & 0xFFFF


def is_stb(word: int, rs: int | None = None) -> bool:
    if (word >> 26) != 38:
        return False
    if rs is not None and ((word >> 21) & 0x1F) != rs:
        return False
    return True


@dataclass
class DispatcherHit:
    base: int
    sid_addrs: dict[int, int]  # SID -> address of its cmpwi

    @property
    def sid_count(self) -> int:
        return len(self.sid_addrs)


def find_boot_dispatcher(data: bytes) -> DispatcherHit | None:
    """Slide a window over the boot region and return the position with the largest
    set of programming-session SIDs in cmpwi/beq pairs."""
    best: DispatcherHit | None = None
    end = min(len(data), BOOT_REGION_END)
    for base in range(0, end - DISPATCHER_WINDOW, 4):
        sid_addrs: dict[int, int] = {}
        for off in range(base, base + DISPATCHER_WINDOW, 4):
            if off + 8 > len(data):
                break
            w1 = read_u32_be(data, off)
            sid = cmpwi_sid(w1)
            if sid is None or sid not in PROGRAMMING_SIDS:
                continue
            w2 = read_u32_be(data, off + 4)
            if not is_beq(w2):
                continue
            if sid not in sid_addrs:
                sid_addrs[sid] = off
        if len(sid_addrs) < 4:
            continue
        if best is None or len(sid_addrs) > best.sid_count:
            anchor = min(sid_addrs.values())
            best = DispatcherHit(base=anchor, sid_addrs=sid_addrs)
    return best


def follow_trampoline_to_handler(data: bytes, trampoline: int) -> int | None:
    """Scan from the trampoline forward to the first `bl` and return its target."""
    addr = trampoline
    for _ in range(16):
        if addr + 4 > len(data):
            return None
        word = read_u32_be(data, addr)
        if is_bl(word):
            return bl_target(addr, word)
        if (word >> 26) == 18:
            return None
        addr += 4
    return None


@dataclass
class HandlerScan:
    li_addrs: list[int]
    li_values: list[int]
    stb_displs: list[int]


def scan_handler_for_response_writes(data: bytes, handler: int, max_words: int = 32) -> HandlerScan | None:
    """Walk forward from the handler entry collecting (li rT, imm) immediates whose
    very next instruction is a `stb rT, displ(rB)` to a monotonically advancing
    displacement. Stop when we've collected 6 such pairs or run out of pattern.
    """
    li_addrs: list[int] = []
    li_values: list[int] = []
    stb_displs: list[int] = []
    addr = handler
    seen_displs: list[int] = []
    for _ in range(max_words * 8):
        if addr + 8 > len(data):
            break
        w1 = read_u32_be(data, addr)
        rt = is_li(w1)
        if rt is not None:
            w2 = read_u32_be(data, addr + 4)
            if is_stb(w2, rs=rt):
                displ = sext16(w2 & 0xFFFF)
                if not seen_displs or displ != seen_displs[-1]:
                    li_addrs.append(addr)
                    li_values.append(li_imm(w1))
                    stb_displs.append(displ)
                    seen_displs.append(displ)
                    addr += 8
                    if len(li_values) >= 6:
                        break
                    continue
        addr += 4
    if len(li_values) < 6:
        return None
    return HandlerScan(li_addrs=li_addrs[:6], li_values=li_values[:6], stb_displs=stb_displs[:6])


def count_cmpwi_27(data: bytes) -> int:
    count = 0
    for off in range(0, len(data) - 3, 4):
        if cmpwi_sid(read_u32_be(data, off)) == 0x27:
            count += 1
    return count


def find_algo92_immediates(data: bytes) -> list[tuple[int, int, int]]:
    """Return (addr, opcode, imm) for every addi/addic/addis (opcodes 12-15) whose
    16-bit immediate is one of the algo-92 constants. Excludes register-field
    collisions from non-immediate opcodes."""
    hits: list[tuple[int, int, int]] = []
    targets = set(ALGO_92_CONSTANTS)
    for off in range(0, len(data) - 3, 4):
        word = read_u32_be(data, off)
        op = (word >> 26) & 0x3F
        if op not in (12, 13, 14, 15):
            continue
        imm = word & 0xFFFF
        if imm in targets:
            hits.append((off, op, imm))
    return hits


def sid_set_str(sids: dict[int, int]) -> str:
    return " ".join(f"${sid:02X}" for sid in sorted(sids))


def disassemble_brief(data: bytes, start: int, count: int) -> list[str]:
    """Very minimal disassembly for verbose mode - just enough to eyeball the handler."""
    lines = []
    for i in range(count):
        addr = start + i * 4
        if addr + 4 > len(data):
            break
        word = read_u32_be(data, addr)
        op = (word >> 26) & 0x3F
        if op == 14:
            rt = (word >> 21) & 0x1F
            ra = (word >> 16) & 0x1F
            simm = sext16(word & 0xFFFF)
            mnem = f"li r{rt}, {simm:#x}" if ra == 0 else f"addi r{rt}, r{ra}, {simm:#x}"
        elif op == 38:
            rs = (word >> 21) & 0x1F
            ra = (word >> 16) & 0x1F
            d = sext16(word & 0xFFFF)
            mnem = f"stb r{rs}, {d}(r{ra})"
        elif op == 15:
            rt = (word >> 21) & 0x1F
            ra = (word >> 16) & 0x1F
            simm = sext16(word & 0xFFFF)
            mnem = f"lis r{rt}, {simm:#x}" if ra == 0 else f"addis r{rt}, r{ra}, {simm:#x}"
        elif is_bl(word):
            mnem = f"bl {bl_target(addr, word):#x}"
        elif (word >> 26) == 18:
            li = sext26(word & 0x03FFFFFC)
            mnem = f"b {addr + li:#x}"
        elif is_cmpwi(word):
            ra = (word >> 16) & 0x1F
            simm = sext16(word & 0xFFFF)
            mnem = f"cmpwi r{ra}, {simm:#x}"
        elif is_beq(word):
            mnem = f"beq {beq_target(addr, word):#x}"
        else:
            mnem = f".long {word:#010x}"
        lines.append(f"    {addr:#08x}: {word:08x}  {mnem}")
    return lines


def analyze_bin(path: str, verbose: bool) -> bool:
    try:
        with open(path, "rb") as f:
            data = f.read()
    except OSError as e:
        print(f"=== {os.path.basename(path)} ===")
        print(f"  ERROR: cannot read: {e}")
        return False

    name = os.path.basename(path)
    size = len(data)
    print(f"=== {name} ({size / 1024 / 1024:.2f} MiB) ===")

    if size % 4 != 0:
        print(f"  WARNING: size {size:#x} not multiple of 4 - truncating to alignment")
        data = data[: size - (size % 4)]

    dispatcher = find_boot_dispatcher(data)
    if dispatcher is None or dispatcher.sid_count < 5:
        cnt = dispatcher.sid_count if dispatcher else 0
        anchor = dispatcher.base if dispatcher else 0
        print(f"  Boot service dispatcher : not found (best window had {cnt} SIDs at {anchor:#08x})")
        print(f"  -> skipping; not a recognizable E38-style boot image")
        return False

    print(
        f"  Boot service dispatcher : {dispatcher.base:#08x} "
        f"({dispatcher.sid_count} SIDs: {sid_set_str(dispatcher.sid_addrs)})"
    )

    if 0x27 not in dispatcher.sid_addrs:
        print(f"  $27 entry               : NOT in dispatcher (no SecurityAccess in this image?)")
        return False

    cmpwi_27_addr = dispatcher.sid_addrs[0x27]
    beq_addr = cmpwi_27_addr + 4
    beq_word = read_u32_be(data, beq_addr)
    if not is_beq(beq_word):
        print(f"  $27 cmpwi at {cmpwi_27_addr:#08x} not followed by beq - bailing")
        return False
    trampoline = beq_target(beq_addr, beq_word)
    print(f"  $27 trampoline          : {trampoline:#08x}")

    handler = follow_trampoline_to_handler(data, trampoline)
    if handler is None:
        print(f"  $27 service handler     : not found (no bl within trampoline window)")
        return False
    print(f"  $27 service handler     : {handler:#08x}")

    if verbose:
        print("    --- trampoline ---")
        for line in disassemble_brief(data, trampoline, 8):
            print(line)
        print("    --- handler entry ---")
        for line in disassemble_brief(data, handler, 32):
            print(line)

    scan = scan_handler_for_response_writes(data, handler)
    if scan is None:
        print(f"  Response packet         : $27 handler signature did not match (no 6x li/stb chain)")
        cmpwi27 = count_cmpwi_27(data)
        print(f"  cmpwi 0x27 count        : {cmpwi27}")
        return False

    status, length, sid_echo, subfn, seed_hi, seed_lo = scan.li_values
    if sid_echo != 0x67:
        print(
            f"  Response packet         : 3rd li was {sid_echo:#04x}, expected 0x67 - aborting (handler does not look like $27)"
        )
        return False

    print(
        f"  Response packet         : length={length}  SID={sid_echo:#04x}  "
        f"subfn={subfn:#04x}  seed={seed_hi:#04x} {seed_lo:#04x}"
    )

    seed_hi_addr = scan.li_addrs[4] + 2
    seed_lo_addr = scan.li_addrs[5] + 2
    print(f"  Seed byte flash offsets : {seed_hi_addr:#08x} (hi)  {seed_lo_addr:#08x} (lo)")

    cmpwi27 = count_cmpwi_27(data)
    note = " (1 dispatcher + others)" if cmpwi27 > 1 else ""
    print(f"  cmpwi 0x27 count        : {cmpwi27}{note}")

    algo92 = find_algo92_immediates(data)
    if not algo92:
        print(f"  algo-92 constants       : not present as immediates")
    else:
        addrs = ", ".join(f"{a:#08x}(0x{i:04X})" for a, _, i in algo92[:8])
        more = "" if len(algo92) <= 8 else f" ... +{len(algo92) - 8} more"
        print(f"  algo-92 constants       : {len(algo92)} hits: {addrs}{more}")

    return True


def main() -> int:
    ap = argparse.ArgumentParser(description="Statically extract the bootloader $27 seed from GM E38-style flash bins.")
    ap.add_argument("bins", nargs="+", help="path(s) to .bin readback(s)")
    ap.add_argument("-v", "--verbose", action="store_true", help="disassemble trampoline + handler entry")
    args = ap.parse_args()

    all_ok = True
    for path in args.bins:
        ok = analyze_bin(path, args.verbose)
        all_ok = all_ok and ok
        print()
    return 0 if all_ok else 1


if __name__ == "__main__":
    sys.exit(main())
