"""
Find ALL service-dispatcher candidates in one bin (not just the densest).
Reports every 256-byte window that holds >= MIN_SIDS distinct GMW3110 SIDs.

Usage:
    python scan_all_dispatchers.py "<path-to-bin>" [min_sids]
"""
import os
import sys
from collections import defaultdict

KNOWN_SIDS = [
    0x10, 0x11, 0x14, 0x1A, 0x20, 0x22, 0x23, 0x27, 0x28, 0x2C, 0x2D,
    0x31, 0x34, 0x35, 0x36, 0x37, 0x3B, 0x3D, 0x3E,
    0xA0, 0xA1, 0xA2, 0xA5, 0xA9, 0xAA, 0xAE,
]


def find_cmpwi_imm(buf, imm):
    n = len(buf)
    end = n - 4
    for i in range(0, end, 4):
        b0 = buf[i]
        if (b0 == 0x2C or b0 == 0x28) and buf[i + 2] == 0x00 and buf[i + 3] == imm:
            yield i


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)
    path = sys.argv[1]
    min_sids = int(sys.argv[2]) if len(sys.argv) > 2 else 6

    with open(path, "rb") as f:
        buf = f.read()
    print(f"File: {path}")
    print(f"Size: {len(buf):,} bytes (0x{len(buf):X})")
    print(f"Threshold: >= {min_sids} distinct SIDs per 256-byte window\n")

    windows = defaultdict(set)
    for sid in KNOWN_SIDS:
        for off in find_cmpwi_imm(buf, sid):
            w = off & ~0xFF
            windows[w].add(sid)

    candidates = sorted(
        [(w, sids) for w, sids in windows.items() if len(sids) >= min_sids],
        key=lambda kv: kv[0],
    )

    if not candidates:
        print("No windows met threshold.")
        return

    print(f"{'Window':12s}  {'#SIDs':>5s}  Region                  SIDs")
    print("-" * 110)
    for w, sids in candidates:
        # Try to find the actual cmpwi $1A anchor in this window
        anchor = None
        for off in find_cmpwi_imm(buf, 0x1A):
            if (off & ~0xFF) == w:
                anchor = off
                break
        anchor_str = f"0x{anchor:08X}" if anchor is not None else f"0x{w:08X}"
        sid_str = " ".join(f"{s:02X}" for s in sorted(sids))
        print(f"0x{w:08X}    {len(sids):>5d}  {anchor_str}            {sid_str}")


if __name__ == "__main__":
    main()
