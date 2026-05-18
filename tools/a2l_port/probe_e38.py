"""
Sanity probe against the Ghidra Bridge.
Confirms which program is loaded, architecture, memory map, function count.
Read-only - makes no changes to the Ghidra project.
"""
import ghidra_bridge

with ghidra_bridge.GhidraBridge(connect_to_port=8701, namespace=globals()) as b:
    prog = currentProgram
    name = prog.getName()
    lang = prog.getLanguage()
    proc = lang.getProcessor().toString()
    endian = "big" if lang.isBigEndian() else "little"
    addr_size = lang.getLanguageDescription().getSize()
    compiler = prog.getCompilerSpec().getCompilerSpecID().getIdAsString()

    print(f"program          : {name}")
    print(f"processor        : {proc}")
    print(f"endian / addr    : {endian} / {addr_size}-bit")
    print(f"compiler spec    : {compiler}")
    print(f"image base       : {prog.getImageBase()}")

    mem = prog.getMemory()
    print()
    print("memory blocks:")
    print(f"  {'name':<20} {'start':>10} {'end':>10} {'size':>10}  flags")
    total_exec = 0
    for blk in mem.getBlocks():
        flags = []
        if blk.isExecute(): flags.append("X")
        if blk.isWrite():   flags.append("W")
        if blk.isRead():    flags.append("R")
        if blk.isInitialized(): flags.append("init")
        else: flags.append("uninit")
        size = blk.getSize()
        if blk.isExecute():
            total_exec += size
        print(f"  {blk.getName():<20} {str(blk.getStart()):>10} {str(blk.getEnd()):>10} {size:>10}  {','.join(flags)}")
    print(f"  total executable bytes: {total_exec:,}")

    fm = prog.getFunctionManager()
    fcount = fm.getFunctionCount()
    print()
    print(f"functions defined: {fcount:,}")

    # find the lowest-address function as a hint at where boot lives
    funcs = list(fm.getFunctions(True))
    if funcs:
        first = funcs[0]
        last = funcs[-1]
        print(f"  first function : {first.getName()} @ {first.getEntryPoint()}")
        print(f"  last function  : {last.getName()} @ {last.getEntryPoint()}")
