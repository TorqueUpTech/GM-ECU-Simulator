using Core.Dps;
using Xunit;

namespace EcuSimulator.Tests.Dps;

public sealed class UtilityFileParserTests
{
    // Real 2011 Silverado E38 utility file extracted during the DPS e2e
    // validation run (see project_dps_e2e_validated memory). 17,328 bytes,
    // bare SPS body (no PTI wrapper).
    // Source from the archive directly so the test isn't fragile to temp-dir
    // cleanup. The archive itself sits in the validated C:\DpsArch\ location.
    private const string FixtureArchive = @"C:\DpsArch\E38_1GCRKSE36BZ158034.zip";

    [Fact]
    public void Parse_E38UtilityFile_DecodesAllSections()
    {
        if (!File.Exists(FixtureArchive)) return;   // silent skip - CI-friendly

        using var archive = Core.Dps.ArchiveExtractor.Extract(FixtureArchive);
        var uf = UtilityFileParser.ParseFile(archive.UtilityFilePath);

        Assert.Null(uf.Pti);
        Assert.Equal(3, uf.Sps.InterpType);
        Assert.Equal(0x07A8, uf.Sps.RoutineSectionOffset);
        Assert.Equal(4094, uf.Sps.DataBytesPerMessage);

        Assert.Equal(121, uf.Instructions.Count);

        var first = uf.Instructions[0];
        Assert.Equal(1, first.Step);
        Assert.Equal(0x01, first.OpCode);
        Assert.Equal(0x11, first.Action[0]);
        Assert.Equal(0xF1, first.Action[1]);
        Assert.Equal(0x00, first.Gotos[0]);
        Assert.Equal(0x02, first.Gotos[1]);
        Assert.Equal(0x00, first.Gotos[2]);

        Assert.Equal(12, uf.Routines.Count);
        Assert.Equal(0x003FC430u, uf.Routines[0].Address);
        Assert.Equal(5440, uf.Routines[0].Data.Length);
        Assert.Equal(new byte[] { 0x04 }, uf.Routines[5].Data);
    }

    [Fact]
    public void Parse_ShortBlob_ThrowsInvalidData()
    {
        var tiny = new byte[10];
        Assert.Throws<InvalidDataException>(() => UtilityFileParser.Parse(tiny));
    }

    [Fact]
    public void Parse_MinimalValidBlob_ParsesEmptySections()
    {
        // 24-byte SPS header, routineSectionOffset=24 (no instructions), no
        // trailing routine bytes. Bare SPS body (no PTI).
        var blob = new byte[24];
        // checksum=0, moduleId=0, utilityPartNo=0, designLevel=0, headerType=0,
        // interpType=3 (GMLAN) at offset 12..14, routineSectionOffset=24 at 14..16,
        // addType=0, dataAddressInfo=0, dataBytesPerMessage=0.
        blob[12] = 0x00; blob[13] = 0x03;
        blob[14] = 0x00; blob[15] = 0x18;

        var uf = UtilityFileParser.Parse(blob);

        Assert.Null(uf.Pti);
        Assert.Equal(3, uf.Sps.InterpType);
        Assert.Equal(24, uf.Sps.RoutineSectionOffset);
        Assert.Empty(uf.Instructions);
        Assert.Empty(uf.Routines);
    }

    [Fact]
    public void OpCodeNames_ContainsCanonicalEntries()
    {
        Assert.Equal("SETUP_GLOBAL_VARIABLES", UtilityFileParser.OpCodeNames[0x01]);
        Assert.Equal("END_WITH_SUCCESS", UtilityFileParser.OpCodeNames[0xFF]);
        Assert.Equal("CHANGE_DATA", UtilityFileParser.OpCodeNames[0x54]);
    }
}
