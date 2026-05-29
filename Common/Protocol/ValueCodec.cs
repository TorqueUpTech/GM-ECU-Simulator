namespace Common.Protocol;

// Engineering-unit ↔ wire-byte conversion. The wire format on GMLAN is
// big-endian per GMW3110; this is the inverse of the DataLogger's
// Channel.cs::ProcessValue (Gm Data Logger_v5_Wpf_WIP/Core/DataObjects/Channel.cs:367).
//
// Engineering value -> raw integer:  raw = round((value - offset) / scalar)
// Raw is then clamped to the type's range and serialised big-endian into the dest span.
public static class ValueCodec
{
    public static void Encode(
        double engValue, double scalar, double offset,
        PidDataType type, int sizeBytes, Span<byte> dest)
    {
        if (dest.Length < sizeBytes)
            throw new ArgumentException($"dest too small: {dest.Length} < {sizeBytes}");

        // Inverse linear scaling. Guard against scalar=0 (treat as identity).
        double raw = scalar != 0 ? (engValue - offset) / scalar : engValue - offset;

        switch (type)
        {
            case PidDataType.Bool:
                dest[0] = raw >= 0.5 ? (byte)1 : (byte)0;
                break;

            case PidDataType.Unsigned:
            case PidDataType.Hex:
            {
                // Any width: the value occupies the low 8 bytes (big-endian); wider fields zero-pad the high bytes,
                // so a 17-byte VIN-style PID or a $2D alias encodes without a 1/2/4-only special case.
                ulong max = sizeBytes >= 8 ? ulong.MaxValue : (1UL << (8 * sizeBytes)) - 1;
                ulong v = raw <= 0 ? 0UL : raw >= max ? max : (ulong)Math.Round(raw);
                WriteBigEndian(dest, sizeBytes, v);
                break;
            }

            case PidDataType.Signed:
            {
                long min = sizeBytes >= 8 ? long.MinValue : -(1L << (8 * sizeBytes - 1));
                long max = sizeBytes >= 8 ? long.MaxValue : (1L << (8 * sizeBytes - 1)) - 1;
                long v = raw <= min ? min : raw >= max ? max : (long)Math.Round(raw);
                // WriteBigEndian emits the low sizeBytes bytes of the two's-complement pattern - correct at any width.
                WriteBigEndian(dest, sizeBytes, (ulong)v);
                break;
            }

            case PidDataType.Ascii:
                // ASCII PIDs return a fixed-length character buffer. The "engValue"
                // semantics don't really apply; for the simulator we emit '?' fill so
                // it's obvious in a log that nothing meaningful was set up.
                dest.Slice(0, sizeBytes).Fill((byte)'?');
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(type));
        }
    }

    private static void WriteBigEndian(Span<byte> dest, int sizeBytes, ulong v)
    {
        for (int i = 0; i < sizeBytes; i++)
        {
            int shift = 8 * (sizeBytes - 1 - i);
            // Bytes beyond the low 8 are always zero (a ulong only carries 8); guard the shift so C#'s mod-64 shift
            // semantics don't wrap a high byte back onto the low byte when sizeBytes > 8.
            dest[i] = shift >= 64 ? (byte)0 : (byte)((v >> shift) & 0xFF);
        }
    }
}
