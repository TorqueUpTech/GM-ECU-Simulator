namespace Core.Dps;

// Ordered list of diagnostic requests the interpreter script will issue.
// Built once at prime time by walking the utility file's instructions and
// pulling out the wire-visible service operations ($22 / $1A / $3B / $AE /
// $27 / $34). The bus log overlay ticks each entry off live as DPS issues
// the matching frame; the diagnostic dialog shows the as-yet-unsatisfied
// tail so the user can see how far into the script DPS got.
public sealed record ExpectedRequest(
    byte InstructionStep,
    byte OpCode,        // GMW3110 SID byte, e.g. $22, $1A, $3B, $AE, $27, $34
    byte SubByte,       // first action byte; for $22 + $1A this is the DID/PID hi byte
    byte SubByte2,      // second action byte; for $22 this is the PID lo byte
    byte[] Action);     // full 4-byte action field, kept for debugging

public sealed class ExpectedRequestLog
{
    public IReadOnlyList<ExpectedRequest> Entries { get; }

    private ExpectedRequestLog(IReadOnlyList<ExpectedRequest> entries) { Entries = entries; }

    // Op-codes that produce on-the-wire requests we can pre-bind to handlers.
    // The interpreter has many more ops ($01 setup, $F3 set-header, $FB/$FC
    // counter+delay, etc.) which never hit the bus.
    private static readonly HashSet<byte> WireOps = new()
    {
        0x10, 0x14, 0x1A, 0x20, 0x22, 0x27, 0x28, 0x34, 0x36, 0x3B,
        0x3E, 0xA2, 0xA5, 0xAA, 0xAE, 0xB0,
    };

    public static ExpectedRequestLog Build(IReadOnlyList<InterpreterInstruction> instructions)
    {
        var list = new List<ExpectedRequest>(instructions.Count);
        foreach (var i in instructions)
        {
            if (!WireOps.Contains(i.OpCode)) continue;
            list.Add(new ExpectedRequest(
                InstructionStep: i.Step,
                OpCode: i.OpCode,
                SubByte: i.Action.Length > 0 ? i.Action[0] : (byte)0,
                SubByte2: i.Action.Length > 1 ? i.Action[1] : (byte)0,
                Action: (byte[])i.Action.Clone()));
        }
        return new ExpectedRequestLog(list);
    }

    public int Count => Entries.Count;
}
