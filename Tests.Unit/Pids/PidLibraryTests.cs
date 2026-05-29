using Common.Pids;
using Xunit;

namespace EcuSimulator.Tests.Pids;

// Round-trip the three embedded encrypted PID catalogues: AES decrypt + gzip
// decompress + CSV parse. The exact row counts here lag the source CSVs by
// one (header row is consumed in ParseCsv) and may drift as new rows are
// added; update the floors below when the packer is rerun against a larger
// CSV. Pinning specific entries also guards the quoted-field CSV parser:
// PID 0x000A's description carries an embedded comma, so if a naive split
// regresses this test breaks instead of silently corrupting the catalogue.
public sealed class PidLibraryTests
{
    [Fact]
    public void Mode01_Catalogue_Loads_With_Expected_Entries()
    {
        var lib = PidLibrary.Mode01;
        Assert.InRange(lib.Count, 30, 1024);

        // 0x05 = engine coolant temperature, signed-word scaled at 1/128
        var ect = lib[0x05];
        Assert.Equal("MEASUREMENT", ect.A2lKind);
        Assert.Equal("SfECTI_T_EngCoolCvrtd", ect.A2lName);
        Assert.Equal("SWORD", ect.DataType);
        Assert.Equal("deg C ", ect.Unit);
        Assert.Equal(0.0078125, ect.Slope!.Value, 10);
    }

    [Fact]
    public void Mode1A_Catalogue_Loads()
    {
        var lib = PidLibrary.Mode1A;
        Assert.InRange(lib.Count, 25, 1024);
    }

    [Fact]
    public void Mode22_Catalogue_Loads_With_Expected_Entries()
    {
        var lib = PidLibrary.Mode22;
        Assert.InRange(lib.Count, 500, 4096);

        // PID 0x000A: description contains an embedded comma inside quotes -
        // proves the CSV parser handles RFC-4180 quoted fields.
        var fuelRail = lib[0x000A];
        Assert.Equal("VeFRPC_p_EstFuelRailPres", fuelRail.A2lName);
        Assert.Contains(",", fuelRail.Description);
        Assert.Equal("kPa ", fuelRail.Unit);
        Assert.Equal(0.03125, fuelRail.Slope!.Value, 10);
    }

    [Fact]
    public void Catalogues_Are_Singleton_Across_Accesses()
    {
        // Lazy<T> guarantees one decrypt/decompress/parse per mode; two
        // accesses must return the same dictionary instance.
        Assert.Same(PidLibrary.Mode22, PidLibrary.Mode22);
    }
}
