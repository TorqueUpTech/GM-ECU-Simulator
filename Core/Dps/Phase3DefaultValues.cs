using System.Text;

namespace Core.Dps;

// "Sensible default" generator for the wizard's Auto-populate button.
// Fills Empty Phase 3 rows with a value of the right length whose byte
// shape matches what a real ECU would return for that DID, without
// pretending to know the real cal-specific value.
//
// The rule per DID:
//   - Known-text DIDs (VIN $90, BCC $B5, Tool $98, etc.) get ASCII
//     padded/truncated to the row's expected length.
//   - Known-numeric DIDs (cal/HW/base part numbers $C0..$CA) get a
//     8-digit ASCII placeholder ("00000001") padded/truncated.
//   - Programming date $99 gets today's date in YYYYMMDD BCD form.
//   - Unknown DIDs get all-0x55 (alternating bits, visually distinct
//     from both 0x00 stubs and 0xFF erased-flash) at the right length.
//   - $22 PIDs get all-0x55 - they're rare in Phase 3 and the bytecode
//     normally covers them via the $54/$53 cascade anyway.
//
// Bytecode rows aren't touched; only Empty rows.
public static class Phase3DefaultValues
{
    public static byte[] For(byte opCode, ushort didOrPid, int length, string? vinFromArchive = null)
    {
        if (length <= 0) return Array.Empty<byte>();

        if (opCode == 0x1A)
        {
            byte did = (byte)didOrPid;
            return ForDid(did, length, vinFromArchive);
        }

        // $22 PID - no semantic guess, return distinguishable filler.
        return Fill(length, 0x55);
    }

    private static byte[] ForDid(byte did, int length, string? vinFromArchive)
    {
        switch (did)
        {
            case 0x90:   // VIN
                return AsciiPad(vinFromArchive ?? PlaceholderVin(), length);

            case 0x99:   // ProgrammingDate (YYYYMMDD ASCII; some implementations expect BCD)
                return AsciiPad(DateTime.UtcNow.ToString("yyyyMMdd"), length);

            case 0x98:   // TesterSerial / programming tool ID
                return AsciiPad("GMECUSIM01", length);

            case 0xB4:   // Mfg Traceability
            case 0xB5:   // BCC
                return AsciiPad("AAAA", length);

            case 0xC0:   // Operating SW ID / cal PN
            case 0xC1:   // End Model
            case 0xC2:   // Base Model
            case 0xC3: case 0xC4: case 0xC5: case 0xC6:
            case 0xC7: case 0xC8: case 0xC9: case 0xCA:
                // 4-byte PNs are BE uint32. Longer fields are ASCII PNs.
                if (length == 4)
                    return new byte[] { 0x00, 0x00, 0x00, 0x01 };   // BE uint32 = 1
                return AsciiPad("00000001", length);

            case 0xD0: case 0xD1: case 0xD2: case 0xD3: case 0xD4:
            case 0xD5: case 0xD6: case 0xD7: case 0xD8: case 0xD9:
            case 0xDA:
                // Alpha codes (paired with $C0..$CA)
                return AsciiPad("AA", length);

            case 0xB0:   // Diagnostic address
                return new byte[] { 0x11 };

            case 0x28:   // Partial VIN (last 6 chars)
                var sourceVin = vinFromArchive ?? PlaceholderVin();
                var tail = sourceVin.Length >= 6 ? sourceVin[^6..] : sourceVin;
                return AsciiPad(tail, length);
        }

        // Unknown DID - alternating bits at the right length.
        return Fill(length, 0x55);
    }

    private static byte[] AsciiPad(string s, int length)
    {
        var bytes = new byte[length];
        var src = Encoding.ASCII.GetBytes(s);
        int copy = Math.Min(src.Length, length);
        Array.Copy(src, 0, bytes, 0, copy);
        // Pad the rest with ASCII space rather than zeros so the result
        // still looks like text in a hex dump.
        for (int i = copy; i < length; i++) bytes[i] = 0x20;
        return bytes;
    }

    private static byte[] Fill(int length, byte value)
    {
        var b = new byte[length];
        for (int i = 0; i < length; i++) b[i] = value;
        return b;
    }

    // 17-char placeholder VIN. No I/O/Q (per ISO 3779 VIN charset).
    private const string PlaceholderVinValue = "1G0FAKEVN00000001";   // 17 chars
    private static string PlaceholderVin() => PlaceholderVinValue;
}
