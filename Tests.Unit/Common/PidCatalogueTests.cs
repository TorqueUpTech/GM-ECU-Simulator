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
