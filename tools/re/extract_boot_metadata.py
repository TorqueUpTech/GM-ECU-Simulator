"""
extract_boot_metadata.py - pull identifying metadata from a GM Global-A
ECU bin (E38/E67/T43 family). Mirrors what HP Tuners' Tune Details panel
shows: VIN, tester serial, OS part number, sub-controller part numbers,
CVNs, security key.

Usage:
    python extract_boot_metadata.py <bin> [--json]

Reads the bin file directly (no Ghidra needed). Output is human-readable
text by default, or single-line JSON with --json for pipelines.

Confirmed against:
    smokeshow_12647991.bin (E38, 2012 Holden HSV LSA, VIN 6G1EX8EW0CL627440)
"""

from __future__ import annotations

import json
import re
import sys
from collections import OrderedDict
from pathlib import Path

# ---------------------------------------------------------------------------
# Known layout for the GM Global-A boot/programming-history block (E38 era).
# All offsets are flash absolute, valid for 2 MiB bins where the boot region
# is 0x0000-0xFFFF and the OS region starts at 0x010000.
#
# The security key location is consistent across E38 bins examined so far;
# if a new bin doesn't match, the metadata scan emits "(not detected)" and
# the layout fields can be widened.
# ---------------------------------------------------------------------------

SECURITY_KEY_OFFSET = 0x0000C134   # halfword, flanked by 0xFFFF padding
CVN_TABLE_OFFSETS   = (0x0000C43A, 0x0000C902)  # duplicate tables, 5 halfwords each
PRIMARY_CVN_OFFSET  = 0x0000C1BC   # Engine Operation CVN appears here too

# VIN characters: ISO 3779 excludes I, O, Q (to avoid 1/0 confusion).
VIN_CHARSET = set("ABCDEFGHJKLMNPRSTUVWXYZ0123456789")

# GM WMI prefixes - keep this list practical not exhaustive.
GM_WMIS = (
    "1G", "2G", "3G",         # GM US/Canada/Mexico
    "5G",                      # GM US (Saturn family)
    "6G", "6H8",               # Holden/HSV Australia
    "KL",                      # GM Korea (Daewoo descendants)
    "W04", "W06",              # Cadillac Europe (rare)
)

# GM Global-A segment header.
# Observed forms:
#   OS region:   "AA" + 3 nulls + 8-digit PN + 8 nulls
#   Cal region:  "AB" + (FF + 2-byte counter) + 8-digit PN + 8 nulls
# Common shape: 2 uppercase letters + 3 separator bytes + 8 ASCII digits.
SEGMENT_HEADER_RE = re.compile(
    rb"([A-Z]{2})[\x00\xff].{2}(\d{8})\x00{2,}",
    re.DOTALL,
)


def find_all(data: bytes, pat: bytes) -> list[int]:
    out, i = [], 0
    while True:
        j = data.find(pat, i)
        if j < 0:
            break
        out.append(j)
        i = j + 1
    return out


def scan_vins(data: bytes) -> list[tuple[int, str]]:
    """Find every 17-char run of valid VIN characters with non-VIN
    neighbours (so we don't capture mid-string slices). Returns
    (offset, vin) tuples."""
    hits = []
    n = len(data)
    i = 0
    while i + 17 <= n:
        if chr(data[i]) in VIN_CHARSET:
            ok = True
            for j in range(1, 17):
                if chr(data[i + j]) not in VIN_CHARSET:
                    ok = False
                    break
            if ok:
                if i > 0 and chr(data[i - 1]) in VIN_CHARSET:
                    ok = False
                if ok and i + 17 < n and chr(data[i + 17]) in VIN_CHARSET:
                    ok = False
            if ok:
                hits.append((i, data[i:i + 17].decode("ascii")))
        i += 1
    return hits


def decode_vin(vin: str) -> dict:
    """Best-effort decode of a GM Global-A VIN. Returns subset that we
    can reasonably interpret without a full WMI database."""
    if len(vin) != 17:
        return {}
    info = {"raw": vin, "wmi": vin[:3]}
    info["is_gm"] = any(vin.startswith(w) for w in GM_WMIS)

    # Model year: position 10 (0-indexed 9). 2010+ letter cycle.
    year_map = {
        "A": 2010, "B": 2011, "C": 2012, "D": 2013, "E": 2014,
        "F": 2015, "G": 2016, "H": 2017, "J": 2018, "K": 2019,
        "L": 2020, "M": 2021, "N": 2022, "P": 2023, "R": 2024,
        "S": 2025, "T": 2026, "V": 2027, "W": 2028, "X": 2029, "Y": 2030,
    }
    info["model_year_char"] = vin[9]
    info["model_year"] = year_map.get(vin[9])
    info["plant_code"] = vin[10]
    info["serial"] = vin[11:]

    # Approximate plant lookup for common GM plants.
    plant_map = {
        "L": "Elizabeth, Adelaide, AU (Holden)",
        "X": "Detroit-Hamtramck, MI",
        "T": "Tonawanda, NY",
        "K": "Kansas City, KS",
        "R": "Arlington, TX",
        "N": "Norwood, OH",
        "S": "Spring Hill, TN",
        "0": "Wilmington, DE",
        "5": "Bowling Green, KY",
    }
    info["plant_name"] = plant_map.get(vin[10], "(unknown)")
    return info


def scan_segment_headers(data: bytes) -> list[tuple[int, str, str]]:
    """Find GM segment headers: letter prefix + 8-digit PN, separated by
    null or space padding. Returns (offset_of_PN, prefix_letter, PN)."""
    out = []
    seen_pns = set()
    for m in SEGMENT_HEADER_RE.finditer(data):
        pn = m.group(2).decode("ascii")
        if pn in seen_pns:
            continue
        seen_pns.add(pn)
        out.append((m.start(2), m.group(1).decode("ascii"), pn))
    return out


def read_halfword_be(data: bytes, off: int) -> int | None:
    if off + 2 > len(data):
        return None
    return (data[off] << 8) | data[off + 1]


def extract_metadata(bin_path: Path) -> OrderedDict:
    data = bin_path.read_bytes()
    n = len(data)

    out = OrderedDict()
    out["file"] = str(bin_path)
    out["size_bytes"] = n
    out["filename_pn_hint"] = re.findall(r"(\d{8})", bin_path.name)

    # ---- segment part numbers --------------------------------------------
    headers = scan_segment_headers(data)
    out["segments"] = [
        {"offset": f"0x{off:08X}", "prefix": prefix, "part_number": pn}
        for off, prefix, pn in headers
    ]
    # The OS PN is the one at 0x1000E (start of OS region).
    out["os_part_number"] = next(
        (pn for off, _, pn in headers if off == 0x1000E),
        headers[0][2] if headers else None,
    )

    # ---- VIN -------------------------------------------------------------
    vins = scan_vins(data)
    # Prefer hits with a known GM WMI; many bins have padding patterns that
    # accidentally pass the VIN-charset check.
    gm_vins = [v for v in vins if any(v[1].startswith(w) for w in GM_WMIS)]
    seen = set()
    uniq = []
    for off, v in gm_vins:
        if v not in seen:
            seen.add(v)
            uniq.append((off, v))
    out["vins"] = [{"offset": f"0x{off:08X}", "vin": v} for off, v in uniq]
    if uniq:
        out["vin"] = decode_vin(uniq[0][1])

    # ---- tester serial (sits just before the VIN in the boot block) -----
    # Heuristic: a 17-byte alphanumeric run with neighbours that are not
    # alphanumeric, located in the boot region.
    serial_re = re.compile(rb"[^A-Z0-9]([A-Z0-9]{17})[^A-Z0-9]")
    serial_hits = []
    for m in serial_re.finditer(data[:0x10000]):
        s = m.group(1).decode("ascii")
        # Don't double-count the VIN.
        if any(s == v for _, v in vins):
            continue
        serial_hits.append((m.start() + 1, s))
    out["tester_serials"] = [
        {"offset": f"0x{off:08X}", "value": s} for off, s in serial_hits
    ]

    # ---- security key ----------------------------------------------------
    key_val = read_halfword_be(data, SECURITY_KEY_OFFSET)
    out["security_key"] = {
        "offset": f"0x{SECURITY_KEY_OFFSET:08X}",
        "value_hex": f"0x{key_val:04X}" if key_val is not None else None,
        "value_bytes": f"{data[SECURITY_KEY_OFFSET]:02X} "
                       f"{data[SECURITY_KEY_OFFSET + 1]:02X}"
                       if key_val is not None else None,
        "context_bytes": data[
            SECURITY_KEY_OFFSET - 4: SECURITY_KEY_OFFSET + 6
        ].hex(" ") if key_val is not None else None,
    }

    # ---- CVNs -----------------------------------------------------------
    # Two redundant tables of 5 sub-controller CVNs each.
    cvns_block = OrderedDict()
    for table_off in CVN_TABLE_OFFSETS:
        block = []
        for k in range(5):
            val = read_halfword_be(data, table_off + k * 2)
            block.append({
                "offset": f"0x{table_off + k * 2:08X}",
                "value_hex": f"0x{val:04X}" if val is not None else None,
            })
        cvns_block[f"table_at_0x{table_off:08X}"] = block
    # Engine-Operation CVN sits standalone earlier in the block.
    eo_val = read_halfword_be(data, PRIMARY_CVN_OFFSET)
    cvns_block["engine_op_primary"] = {
        "offset": f"0x{PRIMARY_CVN_OFFSET:08X}",
        "value_hex": f"0x{eo_val:04X}" if eo_val is not None else None,
    }
    out["cvns"] = cvns_block

    # ---- Main OS CVN (separate location, scan for it) -------------------
    # The Main OS CVN doesn't sit in the 5-entry table; it lives at an
    # offset specific to each OS PN. We can't auto-locate it without more
    # samples, so we flag it as "TODO".
    out["main_os_cvn_offset"] = "TODO - varies per OS; locate manually"

    return out


def render_text(meta: OrderedDict) -> str:
    L = []
    L.append(f"File:       {meta['file']}")
    L.append(f"Size:       {meta['size_bytes']:,} bytes")
    if meta.get("os_part_number"):
        L.append(f"OS PN:      {meta['os_part_number']}")
    L.append("")

    vin = meta.get("vin", {})
    if vin:
        L.append("VIN")
        L.append(f"  Raw:         {vin.get('raw')}")
        L.append(f"  WMI:         {vin.get('wmi')}  (GM: {vin.get('is_gm')})")
        L.append(f"  Model year:  {vin.get('model_year')} "
                 f"(code '{vin.get('model_year_char')}')")
        L.append(f"  Plant:       '{vin.get('plant_code')}' - "
                 f"{vin.get('plant_name')}")
        L.append(f"  Serial:      {vin.get('serial')}")
        L.append("")

    sk = meta.get("security_key", {})
    if sk and sk.get("value_hex"):
        L.append(f"Security Key:  {sk['value_hex']}  "
                 f"({sk['value_bytes']})  @ {sk['offset']}")
        L.append(f"  Context:     {sk['context_bytes']}")
        L.append("")

    L.append("Segment Part Numbers")
    for seg in meta.get("segments", []):
        L.append(f"  {seg['offset']}  '{seg['prefix']}'  {seg['part_number']}")
    L.append("")

    L.append("CVNs (redundant tables in boot region)")
    cvns = meta.get("cvns", {})
    for table_name, items in cvns.items():
        if table_name == "engine_op_primary":
            L.append(f"  Engine-Op primary @ {items['offset']}: "
                     f"{items['value_hex']}")
            continue
        L.append(f"  {table_name}:")
        for it in items:
            L.append(f"    {it['offset']}  {it['value_hex']}")
    L.append("")

    if meta.get("tester_serials"):
        L.append("Tester serial candidates (boot region 17-char ASCII):")
        for it in meta["tester_serials"][:5]:
            L.append(f"  {it['offset']}  {it['value']}")

    return "\n".join(L)


def main(argv: list[str]) -> int:
    if len(argv) < 2:
        print(__doc__)
        return 2

    json_mode = "--json" in argv
    bin_path = Path([a for a in argv[1:] if not a.startswith("--")][0])
    if not bin_path.exists():
        print(f"file not found: {bin_path}", file=sys.stderr)
        return 1

    meta = extract_metadata(bin_path)

    if json_mode:
        print(json.dumps(meta, indent=2))
    else:
        print(render_text(meta))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
