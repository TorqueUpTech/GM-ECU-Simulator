using Core.Dps;
using Xunit;

namespace EcuSimulator.Tests.Dps;

// Unit-level coverage of Phase3Extractor. Synthetic utility files: each
// test builds the minimum instruction sequence needed to exercise one
// boundary / source-resolution rule. Donor handling is gone; rows are
// either Bytecode (from a $53 routine literal) or Empty (zero bytes).
public sealed class Phase3ExtractorTests
{
    private static InterpreterInstruction Inst(byte step, byte op, byte a0, byte a1, byte a2, byte a3)
        => new(step, op, [a0, a1, a2, a3], new byte[10]);

    private static UtilityFile UfWith(InterpreterInstruction[] insts, params byte[][] routines)
    {
        var routineList = routines.Select((bytes, i) =>
            new UtilityRoutine(i, 0x00u, bytes)).ToList();
        var sps = new SpsInterpreterHeader(0, 0, 0, 0, 0, 3, 0, 0, 0, 0);
        return new UtilityFile(Pti: null, Sps: sps, Instructions: insts, Routines: routineList);
    }

    private static PidResponseSolver.SolverResult EmptySolver()
        => new(new Dictionary<ushort, byte[]>(), 0, 0);

    [Fact]
    public void Build_NoCompareDownstream_RowIsEmpty()
    {
        // Single $1A read of DID $C2 with no $53 after - HasCompareDownstream
        // should be false and Source should fall through to Empty.
        var uf = UfWith(new[]
        {
            Inst(0x01, 0xB0, 0x01, 0x00, 0x00, 0x00),    // $B0 cal-block transfer (Phase 2 boundary)
            Inst(0x02, 0x1A, 0xC2, 0x05, 0x00, 0x00),    // $1A C2, store in buf 5
        });
        var m = Phase3Extractor.Build(uf, EmptySolver(), Array.Empty<E38PidRecord>());

        Assert.Single(m.Rows);
        var row = m.Rows[0];
        Assert.Equal(0xC2, row.DidOrPid);
        Assert.False(row.HasCompareDownstream);
        Assert.Equal(Phase3RowSource.Empty, row.Source);
    }

    [Fact]
    public void Build_CompareReadsHaveBytecodeSource()
    {
        // $1A C1 -> buf 3; $53 compares buf 3 against routine[0] literal.
        // Source should be Bytecode, value = routine.
        var expected = new byte[] { 0x12, 0x63, 0x98, 0x35 };
        var uf = UfWith(new[]
        {
            Inst(0x01, 0xB0, 0x01, 0x00, 0x00, 0x00),    // boundary marker
            Inst(0x02, 0x1A, 0xC1, 0x03, 0x00, 0x00),    // $1A C1 -> buf 3
            Inst(0x03, 0x53, 0x03, 0x00, 0x00, 0x01),    // $53 buf 3 vs routine[0]
        }, expected);

        var m = Phase3Extractor.Build(uf, EmptySolver(), Array.Empty<E38PidRecord>());

        Assert.Single(m.Rows);
        var row = m.Rows[0];
        Assert.True(row.HasCompareDownstream);
        Assert.Equal(Phase3RowSource.Bytecode, row.Source);
        Assert.Equal(expected, row.ExpectedValue);
        Assert.Equal(expected.Length, row.ExpectedLength);
    }

    [Fact]
    public void Build_Phase3BoundaryAfterLastB0CalTransfer()
    {
        // Reads BEFORE the last cal-block $B0 (action[0] != 0) should not
        // appear; reads AFTER should.
        var uf = UfWith(new[]
        {
            Inst(0x01, 0x1A, 0xAA, 0x00, 0x00, 0x00),   // pre-Phase 3 - excluded
            Inst(0x02, 0xB0, 0x01, 0x00, 0x00, 0x00),   // cal-block transfer (Phase 2)
            Inst(0x03, 0x1A, 0xBB, 0x00, 0x00, 0x00),   // Phase 3
            Inst(0x04, 0x22, 0x15, 0x5B, 0x01, 0x00),   // Phase 3 $22 PID
        });

        var m = Phase3Extractor.Build(uf, EmptySolver(), Array.Empty<E38PidRecord>());

        Assert.Equal(2, m.Rows.Count);
        Assert.Equal(0xBB, m.Rows[0].DidOrPid);
        Assert.Equal(0x155B, m.Rows[1].DidOrPid);
    }

    [Fact]
    public void Build_KernelLoadB0_DoesNotAdvancePhase3Start()
    {
        // $B0 with action[0] == 0 is a routine-only kernel/loader transfer
        // (Phase 1). Phase3Extractor should treat the read after a kernel
        // $B0 as Phase 3 (whole stream qualifies since there's no cal $B0).
        var uf = UfWith(new[]
        {
            Inst(0x01, 0xB0, 0x00, 0x01, 0x00, 0x04),   // kernel load (action[0] == 0)
            Inst(0x02, 0x1A, 0xC1, 0x00, 0x00, 0x00),   // would be Phase 3
        });
        var m = Phase3Extractor.Build(uf, EmptySolver(), Array.Empty<E38PidRecord>());
        Assert.Single(m.Rows);
        Assert.Equal(0xC1, m.Rows[0].DidOrPid);
    }

    [Fact]
    public void Build_22PidWithSolverResponse_SourceIsBytecode()
    {
        var uf = UfWith(new[]
        {
            Inst(0x01, 0xB0, 0x01, 0x00, 0x00, 0x00),
            Inst(0x02, 0x22, 0x15, 0x5B, 0x03, 0x00),
        });
        var solver = new PidResponseSolver.SolverResult(
            new Dictionary<ushort, byte[]> { [0x155B] = new byte[] { 0x12, 0x34 } },
            SatisfiedCompareCount: 1,
            UnsatisfiableCompareCount: 0);

        var m = Phase3Extractor.Build(uf, solver, Array.Empty<E38PidRecord>());

        Assert.Single(m.Rows);
        var row = m.Rows[0];
        Assert.Equal(0x155B, row.DidOrPid);
        Assert.Equal(Phase3RowSource.Bytecode, row.Source);
        Assert.Equal(new byte[] { 0x12, 0x34 }, row.ExpectedValue);
    }

    [Fact]
    public void Build_CompareOnDifferentBuffer_RowIsEmpty()
    {
        // $1A C1 -> buf 3, but the $53 compares buf 7 (different buffer).
        // The row should NOT pick that compare's literal.
        var routine = new byte[] { 0xDE, 0xAD };
        var uf = UfWith(new[]
        {
            Inst(0x01, 0xB0, 0x01, 0x00, 0x00, 0x00),
            Inst(0x02, 0x1A, 0xC1, 0x03, 0x00, 0x00),
            Inst(0x03, 0x53, 0x07, 0x00, 0x00, 0x01),
        }, routine);

        var m = Phase3Extractor.Build(uf, EmptySolver(), Array.Empty<E38PidRecord>());

        Assert.Single(m.Rows);
        Assert.False(m.Rows[0].HasCompareDownstream);
        Assert.Equal(Phase3RowSource.Empty, m.Rows[0].Source);
    }
}
