using Common.Protocol;
using Xunit;

namespace EcuSimulator.Tests.Protocol;

// VIN check-digit math (ISO 3779). One real reference VIN with an 'X' check digit pins the algorithm down; the rest
// are self-consistency checks on WithCheckDigit / IsCheckDigitValid.
public sealed class VinTests
{
    [Fact]
    public void ComputeCheckDigit_KnownReferenceVin_YieldsX()
    {
        // Widely-used NHTSA worked example: the position-9 check character is 'X'.
        Assert.Equal('X', Vin.ComputeCheckDigit("1M8GDM9AXKP042788"));
        Assert.True(Vin.IsCheckDigitValid("1M8GDM9AXKP042788"));
    }

    [Fact]
    public void WithCheckDigit_ProducesAValidVin()
    {
        var vin = Vin.WithCheckDigit("6G1ZS5EDGR000001");
        Assert.Equal(17, vin.Length);
        Assert.True(Vin.IsCheckDigitValid(vin));
    }

    [Fact]
    public void IsCheckDigitValid_RejectsAWrongCheckDigit()
    {
        var vin = Vin.WithCheckDigit("6G1ZS5EDGR000001");
        char wrong = vin[8] == '5' ? '6' : '5';
        var bad = vin[..8] + wrong + vin[9..];
        Assert.False(Vin.IsCheckDigitValid(bad));
    }
}
