using Common.Signals;
using Xunit;

namespace EcuSimulator.Tests.Signals;

// Exercises the J1979 catalogue's structural pieces - the support-PID bitmask computation and its block-chaining -
// independently of the $01 handler wiring. Hand-computed bitmasks pin down the bit ordering so a regression there is
// caught here rather than only via a full $01 round-trip.
public sealed class J1979CatalogueTests
{
    [Fact]
    public void SupportMask_Block00_SetsBitsForSupportedPids()
    {
        // Only $01 and $0C supported. $01 is the top bit of byte 0; $0C is bit 4 of byte 1. Nothing lives past $20,
        // so the block-boundary bit (PID $20, the LSB of byte 3) stays clear.
        var supported = new HashSet<byte> { 0x01, 0x0C };
        var mask = new byte[4];
        J1979Catalogue.ComputeSupportMask(0x00, supported, mask);
        Assert.Equal(new byte[] { 0x80, 0x10, 0x00, 0x00 }, mask);
    }

    [Fact]
    public void SupportMask_Block00_FlagsBoundaryWhenMapContinues()
    {
        // $33 lives in the next block, so block $00 must flag PID $20 (LSB of byte 3) to keep a tool walking.
        var supported = new HashSet<byte> { 0x01, 0x33 };
        var mask = new byte[4];
        J1979Catalogue.ComputeSupportMask(0x00, supported, mask);
        Assert.Equal(new byte[] { 0x80, 0x00, 0x00, 0x01 }, mask);
    }

    [Fact]
    public void SupportMask_Block20_PlacesPidInItsOwnBlock()
    {
        // $33 is the 19th PID of block $20 ($21..$40): bit 13, i.e. bit 5 of byte 2.
        var supported = new HashSet<byte> { 0x01, 0x33 };
        var mask = new byte[4];
        J1979Catalogue.ComputeSupportMask(0x20, supported, mask);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x20, 0x00 }, mask);
    }

    [Theory]
    [InlineData(0x00, true)]
    [InlineData(0x20, true)]
    [InlineData(0x40, true)]
    [InlineData(0x60, false)]
    public void BitmaskAnswerable_TracksWhereTheMapEnds(byte pid, bool expected)
    {
        // The default catalogue answers PIDs up to $5C, so blocks $00/$20/$40 are reachable but $60 and beyond aren't.
        Assert.Equal(expected, J1979Catalogue.BitmaskAnswerable(pid, J1979Catalogue.DefaultSupported));
    }

    [Fact]
    public void DefaultSupported_IsDataPidsOnly_NeverBitmaskPids()
    {
        // The advertised subset is exactly the data/status PIDs; the structural $X0 masks are never members.
        Assert.Contains((byte)0x0C, J1979Catalogue.DefaultSupported);
        Assert.DoesNotContain((byte)0x00, J1979Catalogue.DefaultSupported);
        Assert.DoesNotContain((byte)0x20, J1979Catalogue.DefaultSupported);
    }

    [Fact]
    public void Decode_AnalogPid_RoundTripsScaleAndUnit()
    {
        // RPM ($0C): 2-byte raw at 0.25 rpm/bit. 0x0BB8 = 3000 raw -> 750 rpm.
        var rpm = J1979Catalogue.Get(0x0C)!;
        Assert.Equal("750 rpm", rpm.Decode(new byte[] { 0x0B, 0xB8 }));

        // Coolant temp ($05): 1 byte, raw - 40 degC. 0x5A = 90 raw -> 50 degC.
        var clt = J1979Catalogue.Get(0x05)!;
        Assert.Equal("50 degC", clt.Decode(new byte[] { 0x5A }));
    }

    [Fact]
    public void Decode_O2Sensor_FoldsNestedBytesIntoOneString()
    {
        // Nested PID $14: byte A = voltage (0.005 V/bit), byte B = short-term fuel trim ((raw-128)*100/128).
        // 0x5A = 90 -> 0.45 V; 0x82 = 130 -> +1.6%.
        var o2 = J1979Catalogue.Get(0x14)!;
        Assert.Equal("0.45 V, STFT +1.6%", o2.Decode(new byte[] { 0x5A, 0x82 }));

        // 0xFF in byte B means the trim half is unused -> voltage only.
        Assert.Equal("0.45 V", o2.Decode(new byte[] { 0x5A, 0xFF }));
    }

    [Fact]
    public void Decode_StatusPid_IsHumanReadable()
    {
        // Fuel system status ($03): 0x02 = closed loop.
        Assert.Equal("closed loop", J1979Catalogue.Get(0x03)!.Decode(new byte[] { 0x02, 0x00 }));
        // Fuel type ($51): 0x01 = Gasoline.
        Assert.Equal("Gasoline", J1979Catalogue.Get(0x51)!.Decode(new byte[] { 0x01 }));
    }
}
