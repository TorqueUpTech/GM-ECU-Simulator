# ghidra_apply_a2l.py - Ghidra/Jython script.
# Reads the TSV produced by a2l_to_tsv.py and applies labels + data types
# at every address that resolves to a defined memory block.
#
# Run from Script Manager: open this file, hit Run. Edit TSV_PATH below
# if the file is somewhere else. The script is idempotent - re-running
# will overwrite labels and re-apply types.
#
# @category GM.ECU
# @runtime Jython

from ghidra.program.model.symbol import SourceType
from ghidra.program.model.data import (
    ByteDataType,
    WordDataType,
    DWordDataType,
    QWordDataType,
    SignedByteDataType,
    SignedWordDataType,
    SignedDWordDataType,
    SignedQWordDataType,
    FloatDataType,
    DoubleDataType,
    ArrayDataType,
)
from ghidra.program.model.listing import CodeUnit

# ---- configure --------------------------------------------------------------
# Edit if your TSV lives elsewhere.
TSV_PATH = (
    r"C:\Users\Nathan\OneDrive\ECA\Resources\Visual Studio\GM ECU Simulator"
    r"\tools\re\out\e38_12647991.tsv"
)
# -----------------------------------------------------------------------------

TYPE_MAP = {
    "byte":   ByteDataType.dataType,
    "sbyte":  SignedByteDataType.dataType,
    "word":   WordDataType.dataType,
    "sword":  SignedWordDataType.dataType,
    "dword":  DWordDataType.dataType,
    "sdword": SignedDWordDataType.dataType,
    "qword":  QWordDataType.dataType,
    "sqword": SignedQWordDataType.dataType,
    "float":  FloatDataType.dataType,
    "double": DoubleDataType.dataType,
}

# Ghidra symbol names: keep alnum + '_'. Replace everything else.
_BAD = set(' .,[](){}<>+-*/\\|"\'!@#$%^&=?:;`~')


def sanitize(name):
    out = []
    for ch in name:
        if ch.isalnum() or ch == "_":
            out.append(ch)
        elif ch in _BAD:
            out.append("_")
        # silently drop anything else
    s = "".join(out)
    # Collapse runs of underscores for readability.
    while "__" in s:
        s = s.replace("__", "_")
    return s.strip("_") or "anon"


def addr_of(hex_str):
    return currentProgram.getAddressFactory().getAddress(hex_str)


def block_contains(addr):
    return currentProgram.getMemory().getBlock(addr) is not None


def apply_row(kind, addr_hex, name, gtype, array_size):
    addr = addr_of(addr_hex)
    if addr is None or not block_contains(addr):
        return "unmapped"

    listing = currentProgram.getListing()
    symtab = currentProgram.getSymbolTable()

    safe_name = sanitize(name)

    # Don't touch code: if there's already an instruction at this address,
    # just attach a label and bail.
    code_unit = listing.getCodeUnitAt(addr)
    if code_unit is not None and code_unit.getMnemonicString() not in (
        "??", "byte", "word", "dword", "qword", "float", "double",
        "ascii", "undefined",
    ):
        # Likely an instruction - label-only.
        try:
            symtab.createLabel(addr, safe_name, SourceType.USER_DEFINED)
        except Exception:
            pass
        return "label_on_code"

    # Pick the data type. Wrap in ArrayDataType for sized arrays.
    base = TYPE_MAP.get(gtype, ByteDataType.dataType)
    if array_size > 1:
        dt = ArrayDataType(base, array_size, base.getLength())
    else:
        dt = base

    # Clear whatever's there (undefined or stale data), then create.
    end = addr.add(dt.getLength() - 1)
    try:
        listing.clearCodeUnits(addr, end, False)
        listing.createData(addr, dt)
    except Exception as e:
        # Most likely cause: overlapping with an instruction or a larger
        # already-defined item. Fall back to label-only.
        try:
            symtab.createLabel(addr, safe_name, SourceType.USER_DEFINED)
        except Exception:
            pass
        return "label_only"

    # Apply the label (don't make it primary unless no symbol exists yet,
    # to avoid clobbering any names you've already set).
    existing = symtab.getPrimarySymbol(addr)
    try:
        sym = symtab.createLabel(addr, safe_name, SourceType.USER_DEFINED)
        if existing is None and sym is not None:
            sym.setPrimary()
    except Exception:
        pass

    return "ok"


def main():
    print("[a2l] reading %s" % TSV_PATH)
    counts = {}
    n_total = 0

    with open(TSV_PATH, "r") as f:
        header = f.readline()  # skip header
        for line in f:
            line = line.rstrip("\n").rstrip("\r")
            if not line:
                continue
            parts = line.split("\t")
            if len(parts) < 5:
                continue
            kind, addr_hex, name, gtype, n = parts[:5]
            try:
                n_arr = int(n)
            except ValueError:
                n_arr = 1

            status = apply_row(kind, addr_hex, name, gtype, n_arr)
            counts[status] = counts.get(status, 0) + 1
            n_total += 1
            if n_total % 2000 == 0:
                print("  ... %d processed  %s" % (n_total, counts))
                if monitor.isCancelled():
                    print("[a2l] cancelled by user")
                    break

    print("[a2l] done: %d rows" % n_total)
    for k, v in sorted(counts.items()):
        print("  %-15s %d" % (k, v))


main()
