"""
Seed Ghidra with branch-table-derived entry points, then run auto-analysis on E38.

Reads byte tables from the bin on disk (much faster than over the bridge), decodes
every PowerPC b/bl target in the first 0x4080 bytes, dedups, and disassembles each
target inside the bin's range. Then triggers Ghidra's full analyzeAll pipeline
synchronously and reports the resulting function count.

Idempotent: re-seeding already-disassembled addresses is a no-op.
"""
import os
import struct
import time
import ghidra_bridge

BIN_PATH = r"C:\Users\Nathan\OneDrive\Cars\VF2 HSV R8 LSA\GM Programming\PCMHacking\ECM\From Smokeshow\12647991.bin"
BIN_SIZE = 0x200000

def decode_b_targets(blob, base):
    """Yield (instruction_addr, target_addr) for every PPC b/bl in blob."""
    for off in range(0, len(blob) - 3, 4):
        w = struct.unpack(">I", blob[off:off+4])[0]
        opcode = (w >> 26) & 0x3F
        if opcode != 18:  # b / ba / bl / bla
            continue
        # 24-bit signed LI field, shifted left 2
        li = w & 0x03FFFFFC
        if li & 0x02000000:
            li -= 0x04000000  # sign extend
        aa = (w >> 1) & 1
        # lk = w & 1  # don't care for target calc
        instr_addr = base + off
        target = li if aa else (instr_addr + li)
        target &= 0xFFFFFFFF
        yield instr_addr, target

def main():
    # Read the on-disk bin so we can decode without bridge chatter.
    print(f"reading bin from disk: {BIN_PATH}")
    with open(BIN_PATH, "rb") as f:
        bin_bytes = f.read()
    print(f"  size: {len(bin_bytes):,} bytes")
    assert len(bin_bytes) == BIN_SIZE, f"unexpected bin size {len(bin_bytes):#x}"

    # Decode branch targets from both trampoline tables + the 0x4000 vector table.
    raw_targets = set()
    for instr_addr, target in decode_b_targets(bin_bytes[0x0000:0x0500], 0x0000):
        raw_targets.add(target)
    for instr_addr, target in decode_b_targets(bin_bytes[0x4000:0x4400], 0x4000):
        raw_targets.add(target)

    # Sanity-filter: must lie inside the bin and be aligned.
    targets = sorted(t for t in raw_targets if 0 <= t < BIN_SIZE and (t & 3) == 0)
    # Always seed the prologue we identified by eye.
    targets = sorted(set(targets) | {0x450})
    print(f"decoded {len(targets)} unique branch targets to seed")

    print(f"connecting to ghidra bridge...")
    with ghidra_bridge.GhidraBridge(connect_to_port=8701, namespace=globals()) as b:
        prog = currentProgram
        fm = prog.getFunctionManager()
        af = prog.getAddressFactory()
        space = af.getDefaultAddressSpace()

        before_count = fm.getFunctionCount()
        print(f"function count before seeding: {before_count}")

        # ghidra_bridge does NOT auto-wrap mutations in transactions -
        # wrap the whole seed-and-analyze block in one explicit transaction.
        txid = prog.startTransaction("a2l-port: seed entries + analyzeAll")
        try:
            # Seed each target as an external entry point + disassemble.
            st = prog.getSymbolTable()
            seeded = 0
            for t in targets:
                addr = space.getAddress(t)
                try:
                    st.addExternalEntryPoint(addr)
                    ok = disassemble(addr)
                    if ok:
                        seeded += 1
                except Exception as e:
                    print(f"  seed @ 0x{t:08X} failed: {e}")
            print(f"seeded/disassembled: {seeded}/{len(targets)}")

            print(f"running analyzeAll() - this may take several minutes...")
            t0 = time.time()
            analyzeAll(prog)
            elapsed = time.time() - t0
            print(f"analyzeAll() returned after {elapsed:.1f}s")

            prog.endTransaction(txid, True)
        except Exception as e:
            prog.endTransaction(txid, False)
            raise

        after_count = fm.getFunctionCount()
        print(f"function count after auto-analysis: {after_count}  (delta +{after_count - before_count})")

        # Quick summary of function distribution.
        ranges = {"boot (0x0-0xFFFF)": 0, "OS (0x10000-0x1FFFFF)": 0}
        for fn in fm.getFunctions(True):
            ep = fn.getEntryPoint().getOffset()
            if ep < 0x10000:
                ranges["boot (0x0-0xFFFF)"] += 1
            else:
                ranges["OS (0x10000-0x1FFFFF)"] += 1
        for k, v in ranges.items():
            print(f"  {k}: {v}")

if __name__ == "__main__":
    main()
