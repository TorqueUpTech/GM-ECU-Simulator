"""Quick status query - how many functions does Ghidra have right now?"""
import ghidra_bridge
with ghidra_bridge.GhidraBridge(connect_to_port=8701, namespace=globals()) as b:
    prog = currentProgram
    fm = prog.getFunctionManager()
    n = fm.getFunctionCount()
    print(f"current function count: {n}")
    if n > 0:
        funcs = list(fm.getFunctions(True))
        print(f"first function : {funcs[0].getName()} @ {funcs[0].getEntryPoint()}")
        print(f"last function  : {funcs[-1].getName()} @ {funcs[-1].getEntryPoint()}")
        boot = sum(1 for f in funcs if f.getEntryPoint().getOffset() < 0x10000)
        osreg = n - boot
        print(f"boot (0x0-0xFFFF) functions: {boot}")
        print(f"OS (0x10000+)     functions: {osreg}")
