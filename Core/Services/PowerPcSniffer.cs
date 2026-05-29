namespace Core.Services;

// Cheap heuristic: does this blob look like compiled PowerPC code?
//
// GM ECUs in scope use two encodings:
//
//   Book E classic (T43, MPC5xx): 32-bit big-endian, 4-byte aligned.
//     blr               = 4E 80 00 20
//     mflr rD           = (7C..7F) ?8 02 A6   (mfspr rD, LR; mask 0xFC1FFFFF == 0x7C0802A6)
//     stwu r1,-N(r1)    = 94 21 ?? ??         (function prologue stack alloc)
//     b / bl            = 0x48 / 0x49 in top byte at a 4-byte boundary
//
//   e200z VLE (E38, E67): mixed 16/32-bit, 2-byte aligned.
//     se_blr            = 00 04
//     se_mflr r0        = 00 80
//     e_stwu r1,disp(r1)= 18 21 ?? ??
//
// A real GM SPS kernel of 4-32 KB contains dozens of returns and a comparable
// number of prologues. Cal blocks are tables of constants and basically never
// hit these patterns at the relevant alignment. The thresholds below are set
// so the false-positive rate against random data is negligible while still
// firing on every kernel we've seen on the wire.
//
// Source notes:
//   - Book E encodings: PowerPC ISA v2.07, Appendix A.
//   - VLE encodings: EREF "A Programmer's Reference for Freescale Power
//     Architecture Processors", VLE chapter (se_blr / se_mflr / e_stwu).
public static class PowerPcSniffer
{
    public readonly record struct Score(
        int ClassicBlr,
        int ClassicMflr,
        int ClassicStwu,
        int ClassicBranches,
        int VleSeBlr,
        int VleSeMflr,
        int VleEStwu);

    public static Score Analyse(ReadOnlySpan<byte> data)
    {
        int blr = 0, mflr = 0, stwu = 0, br = 0;
        int aligned4 = data.Length & ~3;
        for (int i = 0; i < aligned4; i += 4)
        {
            uint w = ((uint)data[i] << 24)
                   | ((uint)data[i + 1] << 16)
                   | ((uint)data[i + 2] << 8)
                   | data[i + 3];

            if (w == 0x4E800020u) { blr++; continue; }
            if ((w & 0xFC1FFFFFu) == 0x7C0802A6u) { mflr++; continue; }
            if ((w & 0xFFFF0000u) == 0x94210000u) { stwu++; continue; }
            // Unconditional branches (b / bl, AA=0 or AA=1) all share opcode 18.
            if ((w & 0xFC000000u) == 0x48000000u) { br++; continue; }
        }

        int vleBlr = 0, vleMflr = 0, vleEStwu = 0;
        int aligned2 = data.Length & ~1;
        for (int i = 0; i < aligned2; i += 2)
        {
            byte hi = data[i], lo = data[i + 1];
            if (hi == 0x00 && lo == 0x04) { vleBlr++; continue; }
            if (hi == 0x00 && lo == 0x80) { vleMflr++; continue; }
            // e_stwu is a 32-bit VLE instr, but it sits on a 2-byte VLE
            // boundary; opcode bits land in the top byte as 0x18 with the
            // following byte's top nibble = 2 (r1 dest) and rA = 1 (r1 src),
            // giving 18 21 .. .. exactly.
            if (i + 3 < data.Length && hi == 0x18 && lo == 0x21) vleEStwu++;
        }

        return new Score(blr, mflr, stwu, br, vleBlr, vleMflr, vleEStwu);
    }

    public static bool IsLikelyCode(ReadOnlySpan<byte> data) => IsLikelyCode(data, out _);

    public static bool IsLikelyCode(ReadOnlySpan<byte> data, out string reason)
    {
        if (data.Length < 256)
        {
            reason = $"blob too small ({data.Length} B)";
            return false;
        }

        var s = Analyse(data);

        // Book E classic: blr and mflr at 4-byte alignment are functionally
        // unique to compiled code. Three of each clears the noise floor for
        // any cal-table-shaped data while comfortably firing on the
        // smallest kernel we'd see on the wire (T43 SPS is ~6 KB).
        if (s.ClassicBlr >= 3 && s.ClassicMflr >= 3)
        {
            reason = $"book-e: blr={s.ClassicBlr}, mflr={s.ClassicMflr}, " +
                     $"stwu={s.ClassicStwu}, b/bl={s.ClassicBranches}";
            return true;
        }

        // VLE: se_blr (00 04) and se_mflr (00 80) are real 16-bit opcodes
        // but their byte patterns can occur in static data. e_stwu (18 21)
        // is the discriminator - it's a function-prologue 32-bit op that
        // basically doesn't appear in tables. Demand all three: enough
        // returns to look like a function-rich blob, multiple entry-point
        // saves, and at least a handful of stack-frame allocations.
        int sizeThreshold = Math.Max(8, data.Length / 1024);
        if (s.VleSeBlr >= sizeThreshold && s.VleSeMflr >= 4 && s.VleEStwu >= 2)
        {
            reason = $"vle: se_blr={s.VleSeBlr}, se_mflr={s.VleSeMflr}, " +
                     $"e_stwu={s.VleEStwu}";
            return true;
        }

        reason = $"no match (book-e blr={s.ClassicBlr}/mflr={s.ClassicMflr}; " +
                 $"vle se_blr={s.VleSeBlr}/se_mflr={s.VleSeMflr}/e_stwu={s.VleEStwu})";
        return false;
    }
}
