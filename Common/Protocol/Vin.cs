namespace Common.Protocol;

// VIN (ISO 3779) check-digit math, kept in Common so the simulator's default VIN can be built well-formed and any
// layer can verify a VIN's position-9 check character. The VIN charset excludes I/O/Q; the check digit is 0-9 or X.
// Position 9 carries weight 0, so whatever sits there never affects its own computation.
public static class Vin
{
    private static readonly int[] Weights = { 8, 7, 6, 5, 4, 3, 2, 10, 0, 9, 8, 7, 6, 5, 4, 3, 2 };

    // Build a valid 17-char VIN from a 16-char core (positions 1-8 then 10-17), inserting the computed check digit at
    // position 9. The core must already be uppercase VIN charset (A-Z 0-9, no I/O/Q).
    public static string WithCheckDigit(string core16)
    {
        if (core16.Length != 16)
            throw new ArgumentException("core must be 16 chars (a VIN without its check digit)", nameof(core16));
        string placeholder = core16[..8] + "0" + core16[8..];
        return core16[..8] + ComputeCheckDigit(placeholder) + core16[8..];
    }

    // The check character for a full 17-char VIN: sum(transliterate * weight) mod 11, where a result of 10 maps to X.
    public static char ComputeCheckDigit(string vin17)
    {
        if (vin17.Length != 17) throw new ArgumentException("VIN must be 17 chars", nameof(vin17));
        int sum = 0;
        for (int i = 0; i < 17; i++) sum += Transliterate(vin17[i]) * Weights[i];
        int r = sum % 11;
        return r == 10 ? 'X' : (char)('0' + r);
    }

    public static bool IsCheckDigitValid(string vin17)
        => vin17.Length == 17 && vin17[8] == ComputeCheckDigit(vin17);

    // ISO 3779 transliteration. Digits map to themselves; letters follow the standard table (I/O/Q are not valid VIN
    // characters and contribute 0, which only matters for malformed input).
    private static int Transliterate(char c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        return c switch
        {
            'A' => 1, 'B' => 2, 'C' => 3, 'D' => 4, 'E' => 5, 'F' => 6, 'G' => 7, 'H' => 8,
            'J' => 1, 'K' => 2, 'L' => 3, 'M' => 4, 'N' => 5, 'P' => 7, 'R' => 9,
            'S' => 2, 'T' => 3, 'U' => 4, 'V' => 5, 'W' => 6, 'X' => 7, 'Y' => 8, 'Z' => 9,
            _ => 0,
        };
    }
}
