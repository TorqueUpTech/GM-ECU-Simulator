namespace Core.Utilities;

/// <summary>
/// Checksum computation engine for flash validation.
/// Supports multiple checksum algorithms used by GM ECUs.
/// </summary>
public static class ChecksumEngine
{
    /// <summary>
    /// Compute CRC-32 (polynomial 0xEDB88320, used by E38 kernel $3D $02).
    /// </summary>
    public static uint ComputeCrc32(byte[] data, int offset, int length)
    {
        const uint Polynomial = 0xEDB88320;
        uint crc = 0xFFFFFFFF;

        for (int i = offset; i < offset + length; i++)
        {
            crc ^= data[i];
            for (int j = 0; j < 8; j++)
            {
                if ((crc & 1) != 0)
                    crc = (crc >> 1) ^ Polynomial;
                else
                    crc >>= 1;
            }
        }

        return crc ^ 0xFFFFFFFF;
    }

    /// <summary>
    /// Compute CRC-16 (polynomial 0xA001, used by some GM calibration regions).
    /// </summary>
    public static ushort ComputeCrc16(byte[] data, int offset, int length)
    {
        const ushort Polynomial = 0xA001;
        ushort crc = 0xFFFF;

        for (int i = offset; i < offset + length; i++)
        {
            crc ^= data[i];
            for (int j = 0; j < 8; j++)
            {
                if ((crc & 1) != 0)
                    crc = (ushort)((crc >> 1) ^ Polynomial);
                else
                    crc >>= 1;
            }
        }

        return crc;
    }

    /// <summary>
    /// Compute simple sum checksum (byte-wise modulo 256).
    /// Used for basic frame validation in boot ROM.
    /// </summary>
    public static byte ComputeSum(byte[] data, int offset, int length)
    {
        byte sum = 0;
        for (int i = offset; i < offset + length; i++)
            sum += data[i];
        return sum;
    }

    /// <summary>
    /// Compute block checksum: XOR of all bytes.
    /// Used by some GM memory verification routines.
    /// </summary>
    public static byte ComputeXor(byte[] data, int offset, int length)
    {
        byte xor = 0;
        for (int i = offset; i < offset + length; i++)
            xor ^= data[i];
        return xor;
    }

    /// <summary>
    /// Validate a CRC-32 checksum stored at a specific offset in the data.
    /// Returns true if computed CRC matches stored value.
    /// </summary>
    public static bool ValidateCrc32(byte[] data, int offset, int dataLength, int checksumOffset)
    {
        if (checksumOffset + 4 > data.Length)
            return false;

        uint computedCrc = ComputeCrc32(data, offset, dataLength);
        uint storedCrc = (uint)((data[checksumOffset] << 24) |
                                (data[checksumOffset + 1] << 16) |
                                (data[checksumOffset + 2] << 8) |
                                data[checksumOffset + 3]);

        return computedCrc == storedCrc;
    }

    /// <summary>
    /// Validate a CRC-16 checksum stored at a specific offset.
    /// </summary>
    public static bool ValidateCrc16(byte[] data, int offset, int dataLength, int checksumOffset)
    {
        if (checksumOffset + 2 > data.Length)
            return false;

        ushort computedCrc = ComputeCrc16(data, offset, dataLength);
        ushort storedCrc = (ushort)((data[checksumOffset] << 8) | data[checksumOffset + 1]);

        return computedCrc == storedCrc;
    }
}

/// <summary>
/// Tracks checksum state for a flash region (e.g., calibration block).
/// Used to validate completion of calibration-only flash operations.
/// </summary>
public class FlashChecksumRegion
{
    /// <summary>Start address of the region in flash.</summary>
    public uint StartAddress { get; set; }

    /// <summary>Length of the region.</summary>
    public uint Length { get; set; }

    /// <summary>Offset within the region where checksum is stored (or 0 if not embedded).</summary>
    public uint ChecksumOffset { get; set; }

    /// <summary>Type of checksum: "crc32", "crc16", "sum", "xor".</summary>
    public string ChecksumType { get; set; } = "crc32";

    /// <summary>Expected checksum value (for verification).</summary>
    public byte[] ExpectedChecksum { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Validate this region's checksum after writes complete.
    /// </summary>
    public bool Validate(byte[] flashData)
    {
        if (StartAddress + Length > flashData.Length)
            return false;

        return ChecksumType switch
        {
            "crc32" when ChecksumOffset > 0 && ChecksumOffset + 4 <= Length =>
                ChecksumEngine.ValidateCrc32(flashData, (int)StartAddress, (int)(Length - 4), (int)(StartAddress + ChecksumOffset)),
            "crc16" when ChecksumOffset > 0 && ChecksumOffset + 2 <= Length =>
                ChecksumEngine.ValidateCrc16(flashData, (int)StartAddress, (int)(Length - 2), (int)(StartAddress + ChecksumOffset)),
            _ => true, // No checksum validation required
        };
    }
}
