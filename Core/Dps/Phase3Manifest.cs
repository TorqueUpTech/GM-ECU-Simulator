namespace Core.Dps;

// One row per Mode $1A or $22 read the utility-file script will issue
// during Phase 3 (post-flash configuration / verification). Built by
// Phase3Extractor at prime time and surfaced through PrimedDataset so
// the wizard's review page can show every read with its expected value
// before commit.
//
// $3B writes are not rows. They affect the bytes that subsequent $1A
// reads return (the simulator stores writes via SetIdentifier, which is
// what the read handler reads from), but they are upstream causes -
// modelling them as rows would double-count compared to the read that
// follows them.
public enum Phase3RowSource
{
    Empty,      // no source has filled this row; zero bytes will be returned
    Bytecode,   // the $53 routine literal the script will compare against
    Bin,        // walker output from a bin the user picked via "Load from bin..."
    Default,    // sensible default value (set by "Auto-populate empty rows" button)
    User,       // overridden by hand in the wizard
}

public sealed record Phase3Row(
    int StepNumber,                  // sequential 1..N within Phase 3 only
    int InstructionIndex,            // position in the full utility-file instruction stream
    byte OpCode,                     // 0x1A or 0x22
    ushort DidOrPid,                 // $1A: high byte zero, low byte = DID; $22: full 16-bit PID
    bool HasCompareDownstream,       // true when a $53 in Phase 3 asserts on this read's buffer
    Phase3RowSource Source,
    int ExpectedLength,
    byte[] ExpectedValue);           // may be Array.Empty when Source == Empty

public sealed record Phase3Manifest(IReadOnlyList<Phase3Row> Rows);
