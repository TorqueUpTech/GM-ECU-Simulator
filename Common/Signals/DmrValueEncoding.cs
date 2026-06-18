using System;

namespace Common.Signals;

// How a DMR slot's (scaled) value is encoded into the 0x6A0 rapid-packet value bytes. The slot's
// RAM parameter has a fixed type/width/endianness in PCMTec's definition; the user picks the matching
// one here so the displayed value is correct. Ford PCMs are PowerPC (big-endian), so the *BE variants
// are the usual choice; the LE variants and integer widths are there for calibration.
public enum DmrValueEncoding
{
    Float32BE,
    Float32LE,
    Int32BE,
    Int32LE,
    UInt32BE,
    UInt32LE,
    Int16BE,
    Int16LE,
    UInt16BE,
    UInt16LE,
    Int8,
    UInt8,
}

public static class DmrValueCodec
{
    /// <summary>Encode <paramref name="value"/> (already scaled+offset) into 1, 2, or 4 bytes per the
    /// chosen format. Integer formats round to nearest and clamp to range. The returned bytes are
    /// written from the start of the slot's value region.</summary>
    public static byte[] Encode(DmrValueEncoding enc, double value) => enc switch
    {
        DmrValueEncoding.Float32BE => Order(BitConverter.GetBytes((float)value), bigEndian: true),
        DmrValueEncoding.Float32LE => Order(BitConverter.GetBytes((float)value), bigEndian: false),
        DmrValueEncoding.Int32BE   => Order(BitConverter.GetBytes((int)RoundClamp(value, int.MinValue, int.MaxValue)), true),
        DmrValueEncoding.Int32LE   => Order(BitConverter.GetBytes((int)RoundClamp(value, int.MinValue, int.MaxValue)), false),
        DmrValueEncoding.UInt32BE  => Order(BitConverter.GetBytes((uint)RoundClamp(value, uint.MinValue, uint.MaxValue)), true),
        DmrValueEncoding.UInt32LE  => Order(BitConverter.GetBytes((uint)RoundClamp(value, uint.MinValue, uint.MaxValue)), false),
        DmrValueEncoding.Int16BE   => Order(BitConverter.GetBytes((short)RoundClamp(value, short.MinValue, short.MaxValue)), true),
        DmrValueEncoding.Int16LE   => Order(BitConverter.GetBytes((short)RoundClamp(value, short.MinValue, short.MaxValue)), false),
        DmrValueEncoding.UInt16BE  => Order(BitConverter.GetBytes((ushort)RoundClamp(value, ushort.MinValue, ushort.MaxValue)), true),
        DmrValueEncoding.UInt16LE  => Order(BitConverter.GetBytes((ushort)RoundClamp(value, ushort.MinValue, ushort.MaxValue)), false),
        DmrValueEncoding.Int8      => new[] { (byte)(sbyte)RoundClamp(value, sbyte.MinValue, sbyte.MaxValue) },
        DmrValueEncoding.UInt8     => new[] { (byte)RoundClamp(value, byte.MinValue, byte.MaxValue) },
        _                          => Order(BitConverter.GetBytes((float)value), true),
    };

    // BitConverter.GetBytes returns host-endian bytes; reverse to land on the requested order.
    private static byte[] Order(byte[] b, bool bigEndian)
    {
        if (bigEndian == BitConverter.IsLittleEndian) Array.Reverse(b);
        return b;
    }

    private static double RoundClamp(double v, double lo, double hi)
    {
        v = Math.Round(v);
        return v < lo ? lo : v > hi ? hi : v;
    }
}
