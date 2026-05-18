"""
Read-only inspection: small bridge probes only (no bulk reads via bridge).
Also reports the on-disk path of the imported bin so bulk scans can use it directly.
"""
import ghidra_bridge

def classify_ppc_word(w):
    op = (w >> 26) & 0x3F
    if w == 0:                  return "ZERO"
    if w == 0xFFFFFFFF:         return "FILL"
    if op == 0x12:              return "b/bl (branch)"
    if op == 0x10:              return "bc (cond branch)"
    if op == 0x0F:              return "lis (load imm shifted)"
    if op == 0x18:              return "ori"
    if op == 0x0E:              return "addi/li"
    if op == 0x20:              return "lwz"
    if op == 0x24:              return "stw"
    if op == 0x25:              return "stwu (frame prologue)"
    if op == 0x1F:              return "X-form ALU/load/store"
    if op == 0x13:              return "XL-form (bclr/bcctr)"
    return f"op=0x{op:02X}"

def hex_dump(bytes_obj, width=16):
    out = []
    for i in range(0, len(bytes_obj), width):
        chunk = bytes_obj[i:i+width]
        hex_part = " ".join(f"{(b & 0xFF):02X}" for b in chunk)
        ascii_part = "".join(chr(b & 0xFF) if 32 <= (b & 0xFF) < 127 else "." for b in chunk)
        out.append(f"  {i:08X}  {hex_part:<{width*3}}  {ascii_part}")
    return "\n".join(out)

with ghidra_bridge.GhidraBridge(connect_to_port=8701, namespace=globals()) as b:
    prog = currentProgram

    print(f"program name : {prog.getName()}")
    print(f"executable   : {prog.getExecutablePath()}")
    pdf = prog.getDomainFile()
    print(f"project file : {pdf}")
    print(f"project path : {pdf.getPathname() if pdf else '<none>'}")
    print()

    af = prog.getAddressFactory()

    # Small probes only.
    probe_offsets = [
        ("file start",              0x00000000, 0x80),
        ("PPC system-reset @0x100", 0x00000100, 0x80),
        ("offset 0x400",            0x00000400, 0x80),
        ("offset 0x1000",           0x00001000, 0x80),
        ("offset 0x4000",           0x00004000, 0x80),
        ("offset 0x10000 (OS?)",    0x00010000, 0x80),
        ("offset 0x100000 (mid)",   0x00100000, 0x40),
        ("offset 0x1FFFC0 (end-)",  0x001FFFC0, 0x40),
    ]

    print("=" * 72)
    print("RAW BYTE INSPECTION - small probes via bridge")
    print("=" * 72)

    for label, off, length in probe_offsets:
        addr = af.getDefaultAddressSpace().getAddress(off)
        try:
            raw = getBytes(addr, length)
            print()
            print(f"--- {label} @ 0x{off:08X} ---")
            print(hex_dump(raw, 16))
            print(f"  PPC opcode classification (first 8 words):")
            for w_idx in range(min(8, length // 4)):
                off_w = w_idx * 4
                w = ((raw[off_w] & 0xFF) << 24) | ((raw[off_w+1] & 0xFF) << 16) | ((raw[off_w+2] & 0xFF) << 8) | (raw[off_w+3] & 0xFF)
                print(f"    +{off_w:02X}  0x{w:08X}  {classify_ppc_word(w)}")
        except Exception as e:
            print(f"--- {label} @ 0x{off:08X} ---  ERROR: {e}")
