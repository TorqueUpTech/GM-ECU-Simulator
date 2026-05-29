using Common.Protocol;
using Xunit;

namespace EcuSimulator.Tests.Protocol;

// ValueCodec must encode a numeric value into a field of any byte width (not just 1/2/4) so PIDs like a 17-byte VIN
// record or a $2D alias work. The value sits in the low 8 bytes big-endian; wider fields zero-pad the high bytes.
public sealed class ValueCodecTests
{
    [Fact]
    public void Encode_TwoByte_Unchanged()
    {
        var d = new byte[2];
        ValueCodec.Encode(3000, scalar: 1, offset: 0, PidDataType.Unsigned, 2, d);
        Assert.Equal(new byte[] { 0x0B, 0xB8 }, d);   // 3000 = 0x0BB8
    }

    [Fact]
    public void Encode_ThreeByte_BigEndianZeroPadded()
    {
        var d = new byte[3];
        ValueCodec.Encode(0x1234, scalar: 1, offset: 0, PidDataType.Unsigned, 3, d);
        Assert.Equal(new byte[] { 0x00, 0x12, 0x34 }, d);
    }

    [Fact]
    public void Encode_SeventeenByte_ValueInLowByte_HighBytesZero()
    {
        var d = new byte[17];
        ValueCodec.Encode(255, scalar: 1, offset: 0, PidDataType.Unsigned, 17, d);
        var expected = new byte[17];
        expected[16] = 0xFF;   // 255 in the lowest byte; the 16 bytes above stay zero
        Assert.Equal(expected, d);
    }

    [Fact]
    public void Encode_Signed_NegativeOneByte_TwosComplement()
    {
        var d = new byte[1];
        ValueCodec.Encode(-1, scalar: 1, offset: 0, PidDataType.Signed, 1, d);
        Assert.Equal(new byte[] { 0xFF }, d);
    }
}
