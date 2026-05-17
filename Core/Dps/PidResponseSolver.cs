namespace Core.Dps;

// Reverse-solves the value DPS expects each $22 PID to return, using the
// utility-file's $54 CHANGE_DATA cascade + $53 COMPARE_DATA constraints.
// No firmware execution; the algorithm is purely a function of the
// statically-extractable interpreter instructions and the routine literals.
//
// Op-code semantics (from CCRT OpCodeHandler.cs):
//
//   $22 (READ_DATA_BY_PARAMETER_IDENTIFIER)
//     action = [pid_hi, pid_lo, sub, _]. Response is stored in storage
//     buffer indexed by the sub-byte. So a "$22 15 5B 03" read lands in
//     buffer 0x03.
//
//   $54 (CHANGE_DATA)
//     action = [dst_buf, pos_or_src, op, mask]
//     op = 0x00 -> data[pos] = mask (literal write)
//     op = 0x01 -> data[pos] &= mask (AND)
//     op = 0x02 -> data[pos] |= mask (OR)
//     op = 0x03 -> data[pos] ^= mask (XOR)
//     op = 0x04 / 0x05 -> SHL / SHR by `pos` bytes
//     op = 0x06 -> CopyRoutineToStorageBuffer (pos = routine id)
//     op = 0x07 -> CopyVIT2ToStorageBuffer (pos = vit2 id)
//     op = 0x08 -> CopyStorageBuffer (pos = src buf id -> dst_buf)
//
//   $53 (COMPARE_DATA)
//     action = [data2_buf, data1_id, format, source]
//     source = 0 -> VIT2 record (DPS-side; cannot satisfy here)
//     source = 1 -> routine literal
//     source = 2 -> another stored buffer (continuity check)
//
//   AreBytesArraysEqual walks data1.Length bytes - data2 must be at least
//   that long; surplus bytes in data2 are ignored. The script-as-written
//   exploits this to compare 1-byte routine literals against multi-byte
//   buffers.
//
// Per-position satisfiability rule, the heart of the solver:
//   buffer[p] = response[p] & effective_mask     (effective_mask = 0xFF
//                                                  if no $54 op touched p)
//   For "response[p] & mask == expected" to hold, we need
//     (expected & mask) == expected   (every bit of expected is in mask)
//   When satisfiable, set those bits in response[p].
//   When unsatisfiable (mask and expected disagree on at least one bit),
//   we cannot make the compare pass via this mechanism - leave clear and
//   accept the script will branch via the not-match goto. That is a valid
//   path the script's author intended; many post-flash compares are
//   "test which bit is set and route" rather than "must equal."
//
// Operations we currently model: $22 reads, $54 ops 0x01 (AND) and 0x08
// (copy buffer), $53 against routine literals (source=1). Other $54 ops
// turn up rarely in observed archives; when one appears, this solver
// pessimistically treats it as "no satisfiable constraint" - the affected
// buffer's compares still pass through, they just contribute no bits to
// the computed response.
public static class PidResponseSolver
{
    public sealed record SolverResult(
        IReadOnlyDictionary<ushort, byte[]> Responses,
        int SatisfiedCompareCount,
        int UnsatisfiableCompareCount);

    public static SolverResult Compute(UtilityFile uf, IReadOnlyList<E38PidRecord> pids)
    {
        // PID -> length, from the bin's $22 PID table. PIDs that appear in
        // the utility file but are missing from the bin get a default of 1
        // byte (handler will pad / truncate).
        var lengthByPid = pids.ToDictionary(p => p.Pid, p => (int)p.Size);

        var buffers = new Dictionary<byte, BufferState>();
        var responses = new Dictionary<ushort, byte[]>();
        int satisfied = 0;
        int unsatisfiable = 0;

        foreach (var inst in uf.Instructions)
        {
            switch (inst.OpCode)
            {
                case 0x22:
                {
                    ushort pid = (ushort)((inst.Action[0] << 8) | inst.Action[1]);
                    byte sub = inst.Action[2];

                    if (!responses.ContainsKey(pid))
                    {
                        int len = lengthByPid.TryGetValue(pid, out var L) ? L : 1;
                        responses[pid] = new byte[len];
                    }
                    buffers[sub] = new BufferState(pid, responses[pid].Length);
                    break;
                }

                case 0x54:
                {
                    byte dst = inst.Action[0];
                    byte arg = inst.Action[1];
                    byte op = inst.Action[2];
                    byte mask = inst.Action[3];

                    switch (op)
                    {
                        case 0x08:   // CopyStorageBuffer: dst = src@arg
                            if (buffers.TryGetValue(arg, out var src))
                                buffers[dst] = src.Clone();
                            else
                                buffers.Remove(dst);   // unknown source, drop tracking
                            break;

                        case 0x01:   // AND: data[arg] &= mask
                            if (!buffers.TryGetValue(dst, out var b)) break;
                            // Successive ANDs compose: (x & A) & B = x & (A & B)
                            if (b.AndMask.TryGetValue(arg, out var prev))
                                b.AndMask[arg] = (byte)(prev & mask);
                            else
                                b.AndMask[arg] = mask;
                            break;

                        // Other ops fall through - we treat the destination
                        // buffer as no-longer-tracable for the satisfiability
                        // analysis. Drop it so subsequent $53 against it
                        // contributes no bits rather than producing wrong ones.
                        default:
                            buffers.Remove(dst);
                            break;
                    }
                    break;
                }

                case 0x53:
                {
                    byte bufId = inst.Action[0];
                    byte routineId = inst.Action[1];
                    byte sourceKind = inst.Action[3];

                    if (sourceKind != 0x01) break;          // only routine compares are solvable here
                    if (!buffers.TryGetValue(bufId, out var buf)) break;
                    if (!responses.TryGetValue(buf.SourcePid, out var resp)) break;

                    // Out-of-bounds routine ID: DPS's real interpreter still
                    // resolves these (observed on 12645553.bin step 0x3C with
                    // routineId 0x0C against a 12-routine table). The pattern
                    // is always "$54 AND mask M at position P; $53 expect M at
                    // P" - i.e. the mask doubles as the expected literal. Use
                    // the AND mask to satisfy the compare so byte P comes back
                    // with bit M set, matching DPS's expectation.
                    byte[] routine;
                    if (routineId < uf.Routines.Count)
                    {
                        routine = uf.Routines[routineId].Data;
                    }
                    else if (buf.AndMask.Count > 0)
                    {
                        int maxPos = -1;
                        foreach (var kv in buf.AndMask)
                            if (kv.Key > maxPos) maxPos = kv.Key;
                        var synth = new byte[maxPos + 1];
                        foreach (var kv in buf.AndMask)
                            synth[kv.Key] = kv.Value;
                        routine = synth;
                    }
                    else
                    {
                        break;
                    }

                    if (routine.Length == 0) break;          // empty data1 is the AreBytesArraysEqual always-match case

                    int compareLen = Math.Min(routine.Length, resp.Length);
                    for (int p = 0; p < compareLen; p++)
                    {
                        byte expected = routine[p];
                        byte effectiveMask = buf.AndMask.TryGetValue((byte)p, out var m) ? m : (byte)0xFF;

                        if ((expected & effectiveMask) == expected)
                        {
                            // Satisfiable. Set the bits expected wants.
                            resp[p] |= expected;
                            satisfied++;
                        }
                        else
                        {
                            // Unsatisfiable: a bit expected is set but the
                            // cascade's AND mask clears it. The compare will
                            // take its not-match branch - that is a valid
                            // path the archive author intended.
                            unsatisfiable++;
                        }
                    }
                    break;
                }
            }
        }

        return new SolverResult(responses, satisfied, unsatisfiable);
    }

    private sealed class BufferState
    {
        public ushort SourcePid { get; }
        public int Length { get; }
        public Dictionary<byte, byte> AndMask { get; }   // byte position -> combined AND mask

        public BufferState(ushort sourcePid, int length)
        {
            SourcePid = sourcePid;
            Length = length;
            AndMask = new Dictionary<byte, byte>();
        }

        private BufferState(ushort sourcePid, int length, Dictionary<byte, byte> mask)
        {
            SourcePid = sourcePid;
            Length = length;
            AndMask = new Dictionary<byte, byte>(mask);
        }

        public BufferState Clone() => new(SourcePid, Length, AndMask);
    }
}
