using Common.Pids;
using Common.Protocol;
using Xunit;

namespace EcuSimulator.Tests.Pids;

// The library->catalogue bridge: PidCatalogue used to be hand-rolled stubs,
// it now decorates entries from the embedded PidLibrary. These tests pin
// the bridge so a future schema or mapping change can't silently strip
// catalogue entries the editor relies on.
public sealed class PidCatalogueTests
{
    [Fact]
    public void Mode22_Catalogue_Matches_Library_Row_Count()
    {
        Assert.Equal(PidLibrary.Mode22.Count, PidCatalogue.Mode22.Count);
        Assert.All(PidCatalogue.Mode22, e => Assert.Equal(PidMode.Mode22, e.Mode));
    }

    [Fact]
    public void Mode22_Catalogue_Carries_Known_Entry_With_Slope_And_Unit()
    {
        // PID 0x0005: SfECTI_T_EngCoolCvrtd, signed-word scaled at 1/128,
        // deg C. The library row's trailing unit space ("deg C ") gets
        // trimmed by FromLibrary; the slope flows through verbatim.
        var ect = PidCatalogue.Mode22.Single(e => e.Identifier == 0x0005);
        Assert.Equal("SfECTI_T_EngCoolCvrtd", ect.Name);
        Assert.Equal(PidSize.Byte, ect.Size);
        Assert.Equal(PidDataType.Signed, ect.DataType);
        Assert.Equal(0.0078125, ect.Scalar, 10);
        Assert.Equal("deg C", ect.Unit);
    }

    [Fact]
    public void Mode22_Catalogue_Preserves_LengthBytes_For_Large_Pids()
    {
        // Find any entry the library marks as >4 bytes. The catalogue must
        // carry the length through so the SelectedCatalogueEntry setter on
        // the editor can preserve fidelity for, e.g., the 17-byte PID 0x155B.
        var large = PidCatalogue.Mode22.Where(e => e.LengthBytes is > 4).Take(3).ToList();
        Assert.NotEmpty(large);
        Assert.All(large, e => Assert.Equal(PidSize.DWord, e.Size));
    }

    [Fact]
    public void Mode22Ford_Catalogue_Matches_Ford_Library_And_Differs_From_Gm()
    {
        Assert.Equal(PidLibrary.Mode22Ford.Count, PidCatalogue.Mode22Ford.Count);
        Assert.All(PidCatalogue.Mode22Ford, e => Assert.Equal(PidMode.Mode22, e.Mode));
        // The Ford SCP dump and the GM A2L set are distinct sources; if they
        // ever collapse to the same list, the persona split has silently broken.
        Assert.NotEqual(PidCatalogue.Mode22, PidCatalogue.Mode22Ford);

        // PID 0x000C: Ford symbol "N", Engine Speed, 2-byte. Pins that the
        // Ford-specific rows (not the GM ones) are what loaded.
        var rpm = PidCatalogue.Mode22Ford.Single(e => e.Identifier == 0x000C);
        Assert.Equal("Engine Speed", rpm.Name);
        Assert.Equal(PidSize.Word, rpm.Size);
    }

    [Fact]
    public void For_Routes_Mode22_By_Persona_Id()
    {
        // ford-uds -> Ford library; GM / runtime-only kernel / unknown -> GM.
        Assert.Same(PidCatalogue.Mode22Ford, PidCatalogue.For(PidMode.Mode22, "ford-uds"));
        Assert.Same(PidCatalogue.Mode22, PidCatalogue.For(PidMode.Mode22, "gmw3110"));
        Assert.Same(PidCatalogue.Mode22, PidCatalogue.For(PidMode.Mode22, "uds-kernel"));
        Assert.Same(PidCatalogue.Mode22, PidCatalogue.For(PidMode.Mode22, null));
    }

    [Fact]
    public void Mode1A_Catalogue_Unions_Spec_Dids_With_Library()
    {
        // Every GMW3110 spec DID must be reachable from the catalogue, even
        // when the library has no row for it (placeholder entry fills the
        // gap with the spec name).
        var ids = PidCatalogue.Mode1A.Select(e => (byte)e.Identifier).ToHashSet();
        foreach (var did in Gmw3110DidNames.KnownDids)
            Assert.Contains(did, ids);
    }
}
