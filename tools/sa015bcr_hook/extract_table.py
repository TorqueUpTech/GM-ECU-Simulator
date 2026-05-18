"""
Parse the PASSWORD TABLE DUMP block in the hook log, validate every
entry, and emit a structured table (JSON + Markdown) for downstream use.

For each algoId 0x00..0xFF we extract:
  - password (62-char ASCII)
  - decoded blob[0..31] (32-byte SHA-seed payload)
  - WORD A (iteration constant, BE16 from blob[32..33])
  - WORD B (expected algoId, BE16 from blob[34..35]) - sanity check vs row label
  - signature (8 bytes, blob[36..43])
  - N when seed[4]==0x06 (default family byte): 0xFF - 0x06 - A

Usage:
  python extract_table.py <hook-log-path>
"""
import base64
import json
import re
import sys
from pathlib import Path


def parse_log(path: Path):
    table = {}
    rx = re.compile(r"algoId=0x([0-9A-Fa-f]{2})\s+\[REAL\]\s+(\S+)")
    for line in path.read_text(encoding="utf-8").splitlines():
        m = rx.search(line)
        if not m:
            continue
        algo_id = int(m.group(1), 16)
        password = m.group(2)
        if len(password) != 62 or password[:2] not in ("01", "03"):
            continue
        table[algo_id] = password
    return table


def decode_entry(password: str) -> dict:
    blob = base64.b64decode(password[2:62])
    return {
        "password": password,
        "length_marker": int(password[:2]),
        "payload_hex": blob[0:32].hex(),
        "A": int.from_bytes(blob[32:34], "big"),
        "B_algoId": int.from_bytes(blob[34:36], "big"),
        "sig_hex": blob[36:44].hex(),
        "N_for_family06": 0xFF - 0x06 - int.from_bytes(blob[32:34], "big"),
    }


def main():
    log_path = Path(sys.argv[1] if len(sys.argv) > 1 else r"C:\DPS\Logs\sa015bcr_hook.txt")
    table = parse_log(log_path)
    if len(table) != 256:
        print(f"WARNING: expected 256 entries, got {len(table)}", file=sys.stderr)

    decoded = {}
    mismatches = []
    for algo_id, pwd in sorted(table.items()):
        entry = decode_entry(pwd)
        decoded[f"0x{algo_id:02X}"] = entry
        if entry["B_algoId"] != algo_id:
            mismatches.append((algo_id, entry["B_algoId"]))

    out_json = log_path.with_suffix(".table.json")
    out_json.write_text(json.dumps(decoded, indent=2))
    print(f"Wrote {len(decoded)} entries to {out_json}")

    # Summary
    print(f"\nB == algoId mismatches: {len(mismatches)}")
    for a, b in mismatches[:10]:
        print(f"  algoId 0x{a:02X} had B = 0x{b:04X}")

    a_values = sorted({e['A'] for e in decoded.values()})
    print(f"\nDistinct A values ({len(a_values)}):")
    for a in a_values[:30]:
        count = sum(1 for e in decoded.values() if e['A'] == a)
        n_06 = 0xFF - 0x06 - a if a <= 0xFF - 0x06 else 'underflow'
        print(f"  A=0x{a:04X} ({a:3d})  N(seed[4]=06)={n_06}  occurs in {count} algoIds")
    if len(a_values) > 30:
        print(f"  ... and {len(a_values)-30} more")


if __name__ == "__main__":
    main()
