namespace Core.Dps;

// Builds a Phase3Manifest from the utility-file's interpreter bytecode.
// Phase 3 = the post-flash configuration/verification stretch DPS executes
// after the last cal block is downloaded; the boundary is the last on-wire
// transfer-like instruction ($B0 BLOCK_TRANSFER_TO_RAM with a non-zero
// cal index, or wire-level $36/$37 if a future archive uses them).
//
// For each $1A and $22 read in that range we emit one row with the
// strongest available value:
//
//   Bytecode  - the $53 COMPARE_DATA routine literal IS the value DPS
//               will assert against; trumps everything else
//   Empty     - no source yet; the wizard's Load-from-bin and
//               Auto-populate buttons promote Empty rows to Bin / Default
//
// Donor handling is removed: the Bin source is now populated by the
// wizard's "Load from bin..." button (running Mode1ADidBinExtractor.Parse
// on a user-picked bin and folding the walker's DID output into the
// matching rows), not by the prime itself. Phase3Extractor is therefore
// donor-free and depends only on the archive's own bytecode.
//
// $3B writes are not rows. They affect the bytes that subsequent $1A
// reads return (the simulator stores writes via SetIdentifier, which is
// what the read handler reads from), but they are upstream causes -
// modelling them as rows would double-count compared to the read that
// follows them.
public static class Phase3Extractor
{
    public static Phase3Manifest Build(
        UtilityFile utility,
        PidResponseSolver.SolverResult solverResult,
        IReadOnlyList<E38PidRecord> knownPids)
    {
        int phase3Start = FindPhase3Start(utility.Instructions);
        var rows = new List<Phase3Row>();
        int step = 0;

        for (int i = phase3Start; i < utility.Instructions.Count; i++)
        {
            var inst = utility.Instructions[i];
            if (inst.OpCode != 0x1A && inst.OpCode != 0x22) continue;
            step++;

            // For $22, action[0:2] is the PID and action[2] is the storage
            // buffer index the response lands in. For $1A, action[0] is the
            // DID and action[1] is the storage buffer index. The buffer
            // index is what $53 later references when asserting.
            byte storageBuf;
            ushort didOrPid;
            if (inst.OpCode == 0x22)
            {
                didOrPid = (ushort)((inst.Action[0] << 8) | inst.Action[1]);
                storageBuf = inst.Action[2];
            }
            else
            {
                didOrPid = inst.Action[0];
                storageBuf = inst.Action[1];
            }

            bool hasCompare = HasCompareAgainstBuffer(utility.Instructions, utility.Routines, fromIndex: i + 1, storageBuf);

            var (source, value, length) = ResolveRowValue(
                opCode: inst.OpCode,
                didOrPid: didOrPid,
                hasCompare: hasCompare,
                storageBuf: storageBuf,
                utility: utility,
                fromIndex: i + 1,
                solverResult: solverResult,
                knownPids: knownPids);

            rows.Add(new Phase3Row(
                StepNumber: step,
                InstructionIndex: i,
                OpCode: inst.OpCode,
                DidOrPid: didOrPid,
                HasCompareDownstream: hasCompare,
                Source: source,
                ExpectedLength: length,
                ExpectedValue: value));
        }

        return new Phase3Manifest(rows);
    }

    // Walk backward looking for the last cal-block $B0 BLOCK_TRANSFER_TO_RAM
    // or wire-level $36/$37 (defensive - real DPS archives drive the wire
    // via $B0, never bare $36). Phase 3 starts on the next instruction.
    //
    // $B0 with action[0] == 0 is a routine-only transfer (Phase 1 kernel
    // load); we skip those so a kernel-then-immediately-Phase-3 archive
    // (no cal blocks at all) keeps the kernel out of Phase 3.
    //
    // If no transfer instructions are present (rare diagnostic-only
    // archives), treat the whole stream as Phase 3.
    private static int FindPhase3Start(IReadOnlyList<InterpreterInstruction> instructions)
    {
        for (int i = instructions.Count - 1; i >= 0; i--)
        {
            var ins = instructions[i];
            byte op = ins.OpCode;
            if (op == 0x36 || op == 0x37) return i + 1;
            if (op == 0xB0 && ins.Action[0] != 0) return i + 1;
        }
        return 0;
    }

    // True when any $53 COMPARE_DATA at fromIndex or later (still within
    // Phase 3) asserts a fixed value on the same storage buffer this read
    // populated. Per the parser, $53 layout is:
    //   action[0] = data2_buf       (the buffer being compared)
    //   action[1] = data1_id        (routine index when source == 1)
    //   action[3] = source kind     (0 = VIT2 record, 1 = routine literal,
    //                                2 = another stored buffer)
    //
    // Only source == 1 with a non-empty routine produces a hard assert: the
    // expected value is fixed, statically extractable, and an empty zero
    // response from the simulator will definitely fail the compare. The
    // other three cases either depend on session state or auto-match:
    //
    //   - source == 0 (VIT2): DPS compares against its own session record
    //     (VIN, tester serial, programming date). Whether it passes depends
    //     on whether DPS wrote that record earlier via $3B, which we can't
    //     statically know.
    //   - source == 2 (buffer-vs-buffer): tests continuity between two
    //     populated buffers. If both were populated by the same source,
    //     they match.
    //   - source == 1 with empty routine: PidResponseSolver's comment notes
    //     "empty data1 is the AreBytesArraysEqual always-match case" - the
    //     compare passes trivially.
    //
    // What we still do NOT detect: a hard compare whose not-match goto
    // leads to a benign continuation (retry counter, alternate branch).
    // Treating every fixed-value compare as fail-critical is a deliberate
    // overshoot - the wizard's warning is "DPS may abort", which a benign
    // not-match goto would survive but the user still wants to see flagged.
    private static bool HasCompareAgainstBuffer(
        IReadOnlyList<InterpreterInstruction> instructions,
        IReadOnlyList<UtilityRoutine> routines,
        int fromIndex,
        byte storageBuf)
    {
        for (int j = fromIndex; j < instructions.Count; j++)
        {
            var ins = instructions[j];
            if (ins.OpCode != 0x53) continue;
            if (ins.Action[0] != storageBuf) continue;
            if (ins.Action[3] != 0x01) continue;                  // skip VIT2 and buffer-vs-buffer
            int routineIdx = ins.Action[1];
            if (routineIdx < 0 || routineIdx >= routines.Count) continue;
            if (routines[routineIdx].Data.Length == 0) continue;  // empty routine = AreBytesArraysEqual always-match
            return true;
        }
        return false;
    }

    // Source resolution at prime time (the baseline before any wizard edits):
    //   1. $53 bytecode literal for compared reads
    //   2. $22 PID solver result (also "Bytecode" - same bytecode origin)
    //   3. Empty (zero bytes, length determined from $3B paired writes
    //      or PID-table size; the wizard's Load-from-bin / Auto-populate
    //      buttons promote these to Bin / Default)
    private static (Phase3RowSource Source, byte[] Value, int Length) ResolveRowValue(
        byte opCode,
        ushort didOrPid,
        bool hasCompare,
        byte storageBuf,
        UtilityFile utility,
        int fromIndex,
        PidResponseSolver.SolverResult solverResult,
        IReadOnlyList<E38PidRecord> knownPids)
    {
        if (hasCompare)
        {
            var literal = FindFirstCompareLiteral(utility, fromIndex, storageBuf);
            if (literal is not null && literal.Length > 0)
                return (Phase3RowSource.Bytecode, literal, literal.Length);
        }

        if (opCode == 0x22 && solverResult.Responses.TryGetValue(didOrPid, out var solved))
            return (Phase3RowSource.Bytecode, solved, solved.Length);

        // No real source - leave as Empty with a length-sized zero buffer.
        // The wizard renders this as "(empty)" in the Source column and
        // warns on commit if HasCompareDownstream is true (DPS will fail
        // the compare against zeros).
        int length = opCode == 0x1A
            ? GuessDidLengthFromWrites(utility, (byte)didOrPid)
            : GuessPidLength(didOrPid, knownPids);
        return (Phase3RowSource.Empty, new byte[length], length);
    }

    private static byte[]? FindFirstCompareLiteral(
        UtilityFile utility,
        int fromIndex,
        byte storageBuf)
    {
        for (int j = fromIndex; j < utility.Instructions.Count; j++)
        {
            var ins = utility.Instructions[j];
            if (ins.OpCode != 0x53) continue;
            if (ins.Action[0] != storageBuf) continue;
            if (ins.Action[3] != 0x01) continue;
            int routineIdx = ins.Action[1];
            if (routineIdx < 0 || routineIdx >= utility.Routines.Count) continue;
            return utility.Routines[routineIdx].Data;
        }
        return null;
    }

    private static int GuessDidLengthFromWrites(UtilityFile utility, byte did)
    {
        foreach (var i in utility.Instructions)
        {
            if (i.OpCode != 0x3B) continue;
            if (i.Action[0] != did) continue;
            byte ac3 = i.Action[3];
            if ((ac3 & 0xF0) == 0x30) return i.Action[2];
        }
        return 8;
    }

    private static int GuessPidLength(ushort pid, IReadOnlyList<E38PidRecord> knownPids)
    {
        foreach (var p in knownPids)
            if (p.Pid == pid) return p.Size;
        return 1;
    }
}
