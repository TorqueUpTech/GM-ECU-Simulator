using Core.Identification;
using Xunit;

namespace EcuSimulator.Tests.Identification;

// Coverage for the archive-OS-module helpers in BinIdentificationReader.
// The dispatcher walker cannot anchor on an archive OS module (the
// dispatcher lives in flash 0x000000..0x010000, the boot region, which
// archive ZIPs do not ship). What IS extractable: the per-module header
// at file offset 0, giving the OS part number + alpha code. That is the
// scope of these tests.
public sealed class ArchiveOsModuleDetectionTests
{
    private const string E38Archive = @"C:\DpsArch\E38_1GCRKSE36BZ158034.zip";

    private static byte[] BuildModuleHeader(string asciiPn, char alpha1 = 'A', char alpha2 = 'A')
    {
        // Replicates the per-module header GM writes at the start of every
        // OS module image. Padded to 1 KB so the input passes length checks.
        var bin = new byte[1024];
        Array.Fill(bin, (byte)0xFF);
        bin[0] = 0xAA;   // CRC16 - arbitrary
        bin[1] = 0xBB;
        bin[2] = 0x00;   // module# = 0 (OS)
        bin[3] = 0x01;   // version byte
        bin[4] = 0x20;   // section marker - the signature byte
        bin[5] = 0x00;
        // BE PN at 6..9 - arbitrary nonzero
        bin[6] = 0x00; bin[7] = 0xC0; bin[8] = 0xDE; bin[9] = 0x5B;
        bin[10] = (byte)alpha1;
        bin[11] = (byte)alpha2;
        // ASCII PN at 0x0E..0x15
        for (int i = 0; i < 8; i++) bin[0x0E + i] = (byte)asciiPn[i];
        return bin;
    }

    [Fact]
    public void LooksLikeArchiveOsModule_AcceptsValidHeader()
    {
        var bin = BuildModuleHeader("12639835");
        Assert.True(BinIdentificationReader.LooksLikeArchiveOsModule(bin));
    }

    [Theory]
    [InlineData(4, 0x21)]    // wrong section marker
    [InlineData(5, 0x01)]    // wrong padding byte
    [InlineData(0x0E, 0x41)] // non-digit at PN start
    [InlineData(0x15, 0x47)] // non-digit at PN end
    public void LooksLikeArchiveOsModule_RejectsMalformedHeader(int corruptOffset, byte corruptValue)
    {
        var bin = BuildModuleHeader("12639835");
        bin[corruptOffset] = corruptValue;
        Assert.False(BinIdentificationReader.LooksLikeArchiveOsModule(bin));
    }

    [Fact]
    public void LooksLikeArchiveOsModule_RejectsAllFF()
    {
        var bin = new byte[1024];
        Array.Fill(bin, (byte)0xFF);
        Assert.False(BinIdentificationReader.LooksLikeArchiveOsModule(bin));
    }

    [Fact]
    public void LooksLikeArchiveOsModule_RejectsShortBin()
    {
        Assert.False(BinIdentificationReader.LooksLikeArchiveOsModule(new byte[10]));
    }

    [Fact]
    public void ReadArchiveOsHeader_ExtractsPnAndAlpha()
    {
        var bin = BuildModuleHeader("12639835", 'A', 'B');
        var header = BinIdentificationReader.ReadArchiveOsHeader(bin);

        Assert.NotNull(header);
        Assert.Equal("12639835", header!.OsPartNumber);
        Assert.Equal("AB", header.AlphaCode);
    }

    [Fact]
    public void ReadArchiveOsHeader_HandlesNonAsciiAlpha()
    {
        var bin = BuildModuleHeader("12639835");
        bin[10] = 0x41;   // 'A'
        bin[11] = 0x00;   // null byte
        var header = BinIdentificationReader.ReadArchiveOsHeader(bin);

        Assert.NotNull(header);
        Assert.Equal("41 00", header!.AlphaCode);
    }

    [Fact]
    public void ReadArchiveOsHeader_ReturnsNullOnNonArchiveBin()
    {
        var bin = new byte[1024];
        Array.Fill(bin, (byte)0xFF);
        Assert.Null(BinIdentificationReader.ReadArchiveOsHeader(bin));
    }

    [Fact]
    public void ReadArchiveOsHeader_OnRealE38Archive_ReturnsKnownPn()
    {
        if (!File.Exists(E38Archive)) return;

        using var archive = Core.Dps.ArchiveExtractor.Extract(E38Archive);
        Assert.NotNull(archive.OsCalFilePath);
        var bytes = File.ReadAllBytes(archive.OsCalFilePath!);

        var header = BinIdentificationReader.ReadArchiveOsHeader(bytes);
        Assert.NotNull(header);
        Assert.Equal("12639835", header!.OsPartNumber);
    }
}
