"""
a2l_to_tsv.py - extract Ghidra-importable symbols from a GM A2L file.

Usage:
    python a2l_to_tsv.py <input.a2l> <output.tsv>

Parses /begin MEASUREMENT, /begin CHARACTERISTIC, and /begin RECORD_LAYOUT
blocks. Emits one TSV row per symbol with the columns:

    kind    address    name    ghidra_type    array_size    description

- kind:        MEAS | CHAR | AXIS
- address:     hex with 0x prefix
- ghidra_type: one of byte, sbyte, word, sword, dword, sdword, float, double
- array_size:  positive int (1 for scalar)
- description: A2L long identifier, tab-stripped

Run under CPython 3.10+ (this machine: Python313).
"""

from __future__ import annotations

import re
import sys
from collections import Counter
from pathlib import Path

# ---------------------------------------------------------------------------
# A2L data type tokens that can appear in MEASUREMENT or RECORD_LAYOUT.
# Map to Ghidra primitive names (matched in ghidra_apply_a2l.py).
# ---------------------------------------------------------------------------
A2L_TO_GHIDRA: dict[str, str] = {
    "UBYTE":        "byte",
    "SBYTE":        "sbyte",
    "UWORD":        "word",
    "SWORD":        "sword",
    "ULONG":        "dword",
    "SLONG":        "sdword",
    "A_UINT64":     "qword",
    "A_INT64":      "sqword",
    "FLOAT32_IEEE": "float",
    "FLOAT64_IEEE": "double",
}

DATATYPE_RE = re.compile(r"\b(" + "|".join(A2L_TO_GHIDRA) + r")\b")


def _strip_quoted(value: str) -> str:
    """Drop surrounding ASAP2 double-quotes if present."""
    v = value.strip()
    if v.startswith('"') and v.endswith('"'):
        return v[1:-1]
    return v


def parse_record_layouts(text: str) -> dict[str, str]:
    """Map RECORD_LAYOUT name -> ghidra type string (best-guess primitive)."""
    layouts: dict[str, str] = {}
    pattern = re.compile(
        r"/begin\s+RECORD_LAYOUT\s+(\S+)(.*?)/end\s+RECORD_LAYOUT",
        re.DOTALL,
    )
    for m in pattern.finditer(text):
        name = m.group(1)
        body = m.group(2)
        # Prefer FNC_VALUES type, fall back to AXIS_PTS_X type.
        fnc = re.search(r"FNC_VALUES\s+\S+\s+(\S+)", body)
        axp = re.search(r"AXIS_PTS_X\s+\S+\s+(\S+)", body)
        a2l_type = (fnc or axp)
        if a2l_type:
            tok = a2l_type.group(1)
            if tok in A2L_TO_GHIDRA:
                layouts[name] = A2L_TO_GHIDRA[tok]
    return layouts


def _iter_blocks(text: str, kind: str):
    """Yield (name, body) for every /begin <kind> ... /end <kind> block."""
    pattern = re.compile(
        rf"/begin\s+{kind}\s+(\S+)(.*?)/end\s+{kind}",
        re.DOTALL,
    )
    for m in pattern.finditer(text):
        yield m.group(1), m.group(2)


def _find_array_size(body: str) -> int:
    m = re.search(r"\bARRAY_SIZE\s+(\d+)", body)
    if m:
        return int(m.group(1))
    m = re.search(r"\bMATRIX_DIM\s+(\d+)", body)
    if m:
        return int(m.group(1))
    return 1


def _find_description(body: str) -> str:
    # First quoted string in the block is the long identifier.
    m = re.search(r'"([^"]*)"', body)
    return m.group(1).strip() if m else ""


def parse_measurements(text: str):
    """MEASUREMENT block: datatype token appears positionally as the first
    bareword on a line of its own, ECU_ADDRESS is keyword-tagged."""
    for name, body in _iter_blocks(text, "MEASUREMENT"):
        addr_m = re.search(r"\bECU_ADDRESS\s+0x([0-9A-Fa-f]+)", body)
        if not addr_m:
            continue  # no concrete address - skip
        addr = int(addr_m.group(1), 16)
        type_m = DATATYPE_RE.search(body)
        gtype = A2L_TO_GHIDRA[type_m.group(1)] if type_m else "byte"
        # Strip the trailing "[x]" placeholder that GM uses for array bases.
        clean_name = name[:-3] if name.endswith("[x]") else name
        clean_name = clean_name[:-3] if clean_name.endswith("[0]") else clean_name
        yield (
            "MEAS",
            addr,
            clean_name,
            gtype,
            _find_array_size(body),
            _find_description(body),
        )


def parse_characteristics(text: str, layouts: dict[str, str]):
    """CHARACTERISTIC block is positional:
       /begin CHARACTERISTIC <name>
         "<desc>"
         <KIND>          # VALUE | VAL_BLK | CURVE | MAP | ASCII | ...
         <0xADDR>        # address
         <RECORD_LAYOUT> # name from RECORD_LAYOUT
         <MAX_DIFF>
         <CM_NAME>
         <LOWER> <UPPER>
         ...
    """
    addr_re = re.compile(r"0x[0-9A-Fa-f]+")
    for name, body in _iter_blocks(text, "CHARACTERISTIC"):
        # Tokenize the lines until we have the 4th positional field (address)
        # and the 5th (record layout). Strip the leading quoted description.
        # Easier: regex over the head section.
        head_re = re.compile(
            r'"[^"]*"\s+(\S+)\s+(0x[0-9A-Fa-f]+)\s+(\S+)',
        )
        m = head_re.search(body)
        if not m:
            continue
        kind_tok, addr_hex, rl_name = m.group(1), m.group(2), m.group(3)
        addr = int(addr_hex, 16)
        gtype = layouts.get(rl_name, "byte")
        clean_name = name[:-3] if name.endswith("[x]") else name
        clean_name = clean_name[:-3] if clean_name.endswith("[0]") else clean_name
        yield (
            "CHAR",
            addr,
            clean_name,
            gtype,
            _find_array_size(body),
            _find_description(body),
        )


def parse_axis_pts(text: str, layouts: dict[str, str]):
    """AXIS_PTS block has the same positional layout as CHARACTERISTIC
    for our purposes (name, desc, kind, address, record_layout)."""
    head_re = re.compile(
        r'"[^"]*"\s+(\S+)\s+(0x[0-9A-Fa-f]+)\s+\S+\s+(\S+)',
    )
    for name, body in _iter_blocks(text, "AXIS_PTS"):
        m = head_re.search(body)
        if not m:
            continue
        _, addr_hex, rl_name = m.group(1), m.group(2), m.group(3)
        addr = int(addr_hex, 16)
        gtype = layouts.get(rl_name, "byte")
        clean_name = name[:-3] if name.endswith("[x]") else name
        clean_name = clean_name[:-3] if clean_name.endswith("[0]") else clean_name
        yield (
            "AXIS",
            addr,
            clean_name,
            gtype,
            _find_array_size(body),
            _find_description(body),
        )


def main(argv: list[str]) -> int:
    if len(argv) != 3:
        print(__doc__)
        return 2

    a2l_path = Path(argv[1])
    tsv_path = Path(argv[2])

    print(f"[a2l] reading {a2l_path} ({a2l_path.stat().st_size:,} bytes)")
    text = a2l_path.read_text(encoding="latin-1")

    print("[a2l] parsing RECORD_LAYOUTs...")
    layouts = parse_record_layouts(text)
    print(f"       {len(layouts)} layouts")

    rows = []
    counts: Counter[str] = Counter()
    type_counts: Counter[str] = Counter()
    skipped_no_addr = 0

    print("[a2l] parsing MEASUREMENTs...")
    for row in parse_measurements(text):
        rows.append(row)
        counts[row[0]] += 1
        type_counts[row[3]] += 1

    print("[a2l] parsing CHARACTERISTICs...")
    for row in parse_characteristics(text, layouts):
        rows.append(row)
        counts[row[0]] += 1
        type_counts[row[3]] += 1

    print("[a2l] parsing AXIS_PTS...")
    for row in parse_axis_pts(text, layouts):
        rows.append(row)
        counts[row[0]] += 1
        type_counts[row[3]] += 1

    # De-duplicate (address, name) - some A2Ls list the same symbol twice.
    seen: set[tuple[int, str]] = set()
    unique_rows = []
    for r in rows:
        key = (r[1], r[2])
        if key in seen:
            continue
        seen.add(key)
        unique_rows.append(r)
    dupes = len(rows) - len(unique_rows)

    # Sort by address for readable TSV.
    unique_rows.sort(key=lambda r: r[1])

    print(f"[a2l] writing {tsv_path}")
    tsv_path.parent.mkdir(parents=True, exist_ok=True)
    with tsv_path.open("w", encoding="utf-8", newline="\n") as f:
        f.write("kind\taddress\tname\tghidra_type\tarray_size\tdescription\n")
        for kind, addr, name, gtype, n, desc in unique_rows:
            # TSV-safe: drop tabs/newlines from description.
            desc_safe = desc.replace("\t", " ").replace("\n", " ").strip()
            f.write(f"{kind}\t0x{addr:08X}\t{name}\t{gtype}\t{n}\t{desc_safe}\n")

    print()
    print(f"  total rows:   {len(unique_rows):,}")
    print(f"  dedup'd:      {dupes:,}")
    print(f"  by kind:      {dict(counts)}")
    print(f"  by type:      {dict(type_counts)}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
