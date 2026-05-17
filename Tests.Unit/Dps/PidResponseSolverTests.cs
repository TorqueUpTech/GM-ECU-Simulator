using Core.Dps;
using Xunit;

namespace EcuSimulator.Tests.Dps;

public sealed class PidResponseSolverTests
{
    // Constructs a minimal UtilityFile whose Instructions list reproduces the
    // Phase 3 cascade fragment we want to exercise. Routines are passed as
    // raw byte[] for readability; other UtilityFile fields are stubs.
    private static UtilityFile BuildUf(InterpreterInstruction[] insts, params byte[][] routines)
    {
        var routineList = routines.Select((bytes, i) =>
            new UtilityRoutine(i, 0x00, bytes)).ToList();
        var sps = new SpsInterpreterHeader(0, 0, 0, 0, 0, 3, 0, 0, 0, 0);
        return new UtilityFile(Pti: null, Sps: sps, Instructions: insts, Routines: routineList);
    }

    private static InterpreterInstruction Inst(byte step, byte op, byte a0, byte a1, byte a2, byte a3)
        => new(step, op, [a0, a1, a2, a3], new byte[10]);

    [Fact]
    public void SatisfiableAndMaskCompare_SetsBitsInResponse()
    {
        // $22 PID 0x1234 sub 03 -> buf 3
        // $54 04 03 08 00       -> copy buf 3 to buf 4
        // $54 04 00 01 10       -> buf 4[0] &= 0x10
        // $53 04 00 00 01       -> compare buf 4 against routine[0] = [0x10]
        // Mask 0x10, expected 0x10: satisfiable -> response[0] gets bit 0x10.
        var uf = BuildUf(new[]
        {
            Inst(1, 0x22, 0x12, 0x34, 0x03, 0x00),
            Inst(2, 0x54, 0x04, 0x03, 0x08, 0x00),
            Inst(3, 0x54, 0x04, 0x00, 0x01, 0x10),
            Inst(4, 0x53, 0x04, 0x00, 0x00, 0x01),
        }, new byte[] { 0x10 });

        var pids = new[] { new E38PidRecord(0x01, 0x1234, 17, 0) };

        var result = PidResponseSolver.Compute(uf, pids);

        Assert.True(result.Responses.ContainsKey(0x1234));
        var resp = result.Responses[0x1234];
        Assert.Equal(17, resp.Length);
        Assert.Equal(0x10, resp[0]);
        Assert.Equal(1, result.SatisfiedCompareCount);
        Assert.Equal(0, result.UnsatisfiableCompareCount);
    }

    [Fact]
    public void UnsatisfiableCompare_LeavesResponseClear_AndCounts()
    {
        // $22 PID 0x1234 sub 03 -> buf 3
        // $54 05 03 08 00       -> copy buf 3 to buf 5
        // $54 05 00 01 02       -> buf 5[0] &= 0x02
        // $53 05 00 00 01       -> compare buf 5 against routine[0] = [0x01]
        // Mask 0x02 can only produce 0x00 or 0x02; expected 0x01 is unreachable.
        var uf = BuildUf(new[]
        {
            Inst(1, 0x22, 0x12, 0x34, 0x03, 0x00),
            Inst(2, 0x54, 0x05, 0x03, 0x08, 0x00),
            Inst(3, 0x54, 0x05, 0x00, 0x01, 0x02),
            Inst(4, 0x53, 0x05, 0x00, 0x00, 0x01),
        }, new byte[] { 0x01 });

        var pids = new[] { new E38PidRecord(0x01, 0x1234, 4, 0) };

        var result = PidResponseSolver.Compute(uf, pids);

        Assert.Equal(0x00, result.Responses[0x1234][0]);
        Assert.Equal(0, result.SatisfiedCompareCount);
        Assert.Equal(1, result.UnsatisfiableCompareCount);
    }

    [Fact]
    public void PidNotInBinTable_RegistersAtDefaultLength()
    {
        var uf = BuildUf(new[]
        {
            Inst(1, 0x22, 0x99, 0x99, 0x03, 0x00),
        });

        var result = PidResponseSolver.Compute(uf, Array.Empty<E38PidRecord>());

        // PID not in the bin table; solver still registers a positive-shape
        // response so the simulator does not return NRC $31.
        Assert.True(result.Responses.ContainsKey(0x9999));
        Assert.Single(result.Responses[0x9999]);   // default length = 1 byte
    }

    [Fact]
    public void OutOfBoundsRoutine_FallsBackToAndMaskAsExpected()
    {
        // 12645553.bin's step 0x3C does this: $54 ANDs buf 3 byte 0 with $10,
        // then $53 compares buf 3 against routine $0C - but the table only
        // has 12 routines (idx 0..11). DPS's real interpreter still resolves
        // it (compare passes when byte 0 == $10). Solver heuristic: when the
        // routine is out of bounds, treat the AND mask itself as the literal
        // expected value, which matches every observed case.
        var uf = BuildUf(new[]
        {
            Inst(1, 0x22, 0x15, 0x5B, 0x03, 0x00),
            Inst(2, 0x54, 0x03, 0x00, 0x01, 0x10),  // buf 3[0] &= 0x10
            Inst(3, 0x53, 0x03, 0x0C, 0x00, 0x01),  // compare vs out-of-bounds routine
        }, new byte[] { 0x00 });  // routine[0] exists but routine[$0C] doesn't

        var pids = new[] { new E38PidRecord(0x01, 0x155B, 17, 0) };

        var result = PidResponseSolver.Compute(uf, pids);

        Assert.True(result.Responses.ContainsKey(0x155B));
        Assert.Equal(0x10, result.Responses[0x155B][0]);
        Assert.Equal(1, result.SatisfiedCompareCount);
    }

    [Fact]
    public void VinCompareAgainstVit2_Ignored()
    {
        // $53 with source=0 (VIT2) cannot be satisfied here. The solver
        // ignores it and does NOT count it as satisfied or unsatisfiable.
        var uf = BuildUf(new[]
        {
            Inst(1, 0x22, 0x12, 0x34, 0x07, 0x00),
            Inst(2, 0x53, 0x07, 0x41, 0x00, 0x00),  // VIT2 source
        });

        var result = PidResponseSolver.Compute(uf, new[] { new E38PidRecord(0x01, 0x1234, 17, 0) });

        Assert.Equal(0, result.SatisfiedCompareCount);
        Assert.Equal(0, result.UnsatisfiableCompareCount);
    }

    [Fact]
    public void RealE38Archive_ProducesNonEmptyResponseFor155B()
    {
        const string archive = @"C:\DpsArch\E38_1GCRKSE36BZ158034.zip";
        if (!File.Exists(archive)) return;

        var dataset = ArchivePrimer.Prime(archive);

        // PID 0x155B is the keystone Phase 3 PID. The solver MUST register
        // it (length from the bin's PID table) so DPS gets a positive
        // response instead of NRC $31 - the exact failure mode we saw in
        // the 19:28:47 bus log.
        Assert.True(dataset.SolverResult.Responses.ContainsKey(0x155B));
        var pid155B = dataset.SolverResult.Responses[0x155B];
        Assert.Equal(17, pid155B.Length);

        // The compares against 0x155B in this archive are mostly unsatisfiable
        // by design (the script branches on bit-tests). We at least exercise
        // the solver - some cascade work happened.
        Assert.True(dataset.SolverResult.UnsatisfiableCompareCount > 0);
    }
}
