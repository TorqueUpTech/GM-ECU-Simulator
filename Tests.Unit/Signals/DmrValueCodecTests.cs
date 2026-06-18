using Common.Signals;
using Xunit;

namespace EcuSimulator.Tests.Signals;

public sealed class DmrValueCodecTests
{
    [Fact]
    public void Float32BE_750_IsExpectedBytes()
        => Assert.Equal(new byte[] { 0x44, 0x3B, 0x80, 0x00 }, DmrValueCodec.Encode(DmrValueEncoding.Float32BE, 750.0));

    [Fact]
    public void Float32LE_IsReverseOfBE()
        => Assert.Equal(new byte[] { 0x00, 0x80, 0x3B, 0x44 }, DmrValueCodec.Encode(DmrValueEncoding.Float32LE, 750.0));

    [Fact]
    public void UInt16BE_3200_IsBigEndian()
        => Assert.Equal(new byte[] { 0x0C, 0x80 }, DmrValueCodec.Encode(DmrValueEncoding.UInt16BE, 3200));

    [Fact]
    public void UInt16LE_3200_IsLittleEndian()
        => Assert.Equal(new byte[] { 0x80, 0x0C }, DmrValueCodec.Encode(DmrValueEncoding.UInt16LE, 3200));

    [Fact]
    public void Int16BE_Negative_IsTwosComplement()
        => Assert.Equal(new byte[] { 0xFF, 0xFF }, DmrValueCodec.Encode(DmrValueEncoding.Int16BE, -1));

    [Fact]
    public void UInt8_ClampsAndRounds()
    {
        Assert.Equal(new byte[] { 0xFF }, DmrValueCodec.Encode(DmrValueEncoding.UInt8, 70000));   // clamp to 255
        Assert.Equal(new byte[] { 0x00 }, DmrValueCodec.Encode(DmrValueEncoding.UInt8, -5));       // clamp to 0
        Assert.Equal(new byte[] { 0x0B }, DmrValueCodec.Encode(DmrValueEncoding.UInt8, 10.6));     // round to 11
    }

    [Fact]
    public void Int32BE_RoundsToNearest()
        => Assert.Equal(new byte[] { 0x00, 0x00, 0x04, 0xD3 }, DmrValueCodec.Encode(DmrValueEncoding.Int32BE, 1234.7));  // 1235 = 0x4D3
}
