"""
Scan every .bin in a folder and report where the PowerPC service dispatcher
lives, using the same heuristic as Core/Identification/BinIdentificationReader.cs
TryFindServiceDispatcher: cluster cmpwi/cmplwi against known GMW3110 SIDs in a
256-byte window and pick the densest window.

Usage:
    python scan_dispatcher_offsets.py "<folder>"
"""
import os
import struct
import sys
from collections import defaultdict

KNOWN_SIDS = [
    0x10, 0x11, 0x14, 0x1A, 0x20, 0x22, 0x23, 0x27, 0x28, 0x2C, 0x2D,
    0x31, 0x34, 0x35, 0x36, 0x37, 0x3B, 0x3D, 0x3E,
    0xA0, 0xA1, 0xA2, 0xA5, 0xA9, 0xAA, 0xAE,
]


def find_cmpwi_imm(buf, imm):
    """Yield 4-byte-aligned offsets where cmpwi/cmplwi rA, <imm> appears.

    PowerPC encoding: opcode byte is 0x2C (cmpwi) or 0x28 (cmplwi). The two
    low bytes hold the 16-bit signed immediate; we only care about positive
    SID-sized values so byte[2] must be 0x00.
    """
    n = len(buf)
    end = n - 4
    for i in range(0, end, 4):
        b0 = buf[i]
        if (b0 == 0x2C or b0 == 0x28) and buf[i + 2] == 0x00 and buf[i + 3] == imm:
            yield i


def find_dispatcher(buf):
    """Return (anchor_offset, sids_found, window_start) or None."""
    windows = defaultdict(set)
    for sid in KNOWN_SIDS:
        for off in find_cmpwi_imm(buf, sid):
            w = off & ~0xFF
            windows[w].add(sid)

    if not windows:
        return None

    best_w, best_sids = max(windows.items(), key=lambda kv: (len(kv[1]), -kv[0]))
    if len(best_sids) < 4:
        return None

    # Anchor on the first $1A cmpwi inside the window, else any cmpwi.
    anchor = None
    for off in find_cmpwi_imm(buf, 0x1A):
        if (off & ~0xFF) == best_w:
            anchor = off
            break
    if anchor is None:
        for sid in sorted(best_sids):
            for off in find_cmpwi_imm(buf, sid):
                if (off & ~0xFF) == best_w:
                    anchor = off
                    break
            if anchor is not None:
                break

    return anchor, sorted(best_sids), best_w


def looks_like_archive_os(buf):
    """Same signature as BinIdentificationReader.LooksLikeArchiveOsModule."""
    if len(buf) < 22:
        return False
    if buf[4] != 0x20 or buf[5] != 0x00:
        return False
    for i in range(0x0E, 0x16):
        if not (0x30 <= buf[i] <= 0x39):
            return False
    return True


def classify_region(offset):
    """The convention: flash 0x000000..0x010000 is the boot region."""
    if offset < 0x010000:
        return "BOOT"
    return "OS  "


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)
    folder = sys.argv[1]
    if not os.path.isdir(folder):
        print(f"Not a directory: {folder}", file=sys.stderr)
        sys.exit(2)

    files = sorted(f for f in os.listdir(folder) if f.lower().endswith(".bin"))
    if not files:
        print("No .bin files found.")
        return

    print(f"Scanning {len(files)} bin(s) in {folder}\n")
    print(f"{'File':60s}  {'Size':>10s}  {'Dispatcher':>11s}  Region  {'~0x6900?':>8s}  Window     #SIDs  SIDs (subset)")
    print("-" * 160)

    near_boot_target = 0x006900
    tolerance = 0x800  # +-2 KiB around the expected anchor

    for name in files:
        path = os.path.join(folder, name)
        with open(path, "rb") as f:
            buf = f.read()
        size = len(buf)
        archive = looks_like_archive_os(buf)

        result = find_dispatcher(buf)
        if result is None:
            print(f"{name[:60]:60s}  {size:10d}  {'-':>11s}  {'-':6s}  {'-':>8s}  {'-':9s}  {'-':>5s}  no dispatcher found"
                  + ("  [archive OS module - no boot region]" if archive else ""))
            continue

        anchor, sids, window = result
        region = classify_region(anchor)
        near = "YES" if abs(anchor - near_boot_target) <= tolerance else "no"
        sid_sample = ",".join(f"{s:02X}" for s in sids[:10]) + ("..." if len(sids) > 10 else "")
        note = "  [archive OS module]" if archive else ""
        print(f"{name[:60]:60s}  {size:10d}  0x{anchor:08X}  {region}  {near:>8s}  0x{window:06X}  {len(sids):5d}  {sid_sample}{note}")


if __name__ == "__main__":
    main()
