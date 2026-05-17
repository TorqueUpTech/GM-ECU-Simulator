namespace Core.Dps;

// Routine-literal lookup. The utility file's routine section carries the
// "expected" side of $53 COMPARE_DATA when action[3]=1; this thin wrapper
// gives the rest of the prime pipeline a typed key/value interface over
// what is otherwise an indexed byte[] list.
public sealed class ExpectedValueTable
{
    private readonly IReadOnlyList<UtilityRoutine> routines;

    public ExpectedValueTable(IReadOnlyList<UtilityRoutine> routines)
    {
        this.routines = routines;
    }

    public int Count => routines.Count;

    // Returns null when the routine id is out of range. Callers (notably the
    // Phase 3 $53 dispatcher) treat null as "the script references a routine
    // the file does not contain" - logged as a flag, not an exception.
    public byte[]? Get(int routineIndex)
    {
        if (routineIndex < 0 || routineIndex >= routines.Count) return null;
        return routines[routineIndex].Data;
    }

    public uint? AddressFor(int routineIndex)
    {
        if (routineIndex < 0 || routineIndex >= routines.Count) return null;
        return routines[routineIndex].Address;
    }
}
