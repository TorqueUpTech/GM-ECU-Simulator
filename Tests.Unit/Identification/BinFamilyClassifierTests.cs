using Core.Identification;
using System.Text;
using Xunit;

namespace EcuSimulator.Tests.Identification;

// Coverage for BinFamilyClassifier. Lift of DetectFamily out of
// Mode1ADidBinExtractor; the synthetic-marker tests below assert each
// family's signature in isolation so a regression in the lift (or a
// future marker addition) is caught here rather than via the dispatcher
// walker's much heavier integration tests.
public sealed class BinFamilyClassifierTests
{
    [Fact]
    public void Classify_TooSmallBin_IsUnknown()
    {
        Assert.Equal(BinFamilyClassifier.Family.Unknown,
            BinFamilyClassifier.Classify(new byte[64]));
    }

    [Fact]
    public void Classify_AllZeros_IsUnknown()
    {
        // 1 MiB of zeros - no markers, no VIN descriptor, no family.
        Assert.Equal(BinFamilyClassifier.Family.Unknown,
            BinFamilyClassifier.Classify(new byte[0x100000]));
    }

    [Fact]
    public void Classify_AllFFErasedFlash_IsUnknown()
    {
        var bin = new byte[0x100000];
        Array.Fill(bin, (byte)0xFF);
        Assert.Equal(BinFamilyClassifier.Family.Unknown,
            BinFamilyClassifier.Classify(bin));
    }

    [Fact]
    public void Classify_T43Marker_IsT43()
    {
        // T43 signature is the "BOSCH TC19.12" ASCII marker. Plant it
        // anywhere in the image; the scan is whole-image.
        var bin = new byte[0x100000];
        WriteAscii(bin, 0x1FF80, "BOSCH TC19.12");
        Assert.Equal(BinFamilyClassifier.Family.T43,
            BinFamilyClassifier.Classify(bin));
    }

    [Fact]
    public void Classify_VinDescriptorAtC0AcWithBosch_IsE67()
    {
        // E67: 25 printable ASCII bytes at 0xC0AC + a BOSCH marker
        // somewhere AND no DELPHI marker. Mimics a real E67 PCM image.
        var bin = new byte[0x100000];
        WriteAscii(bin, 0xC0AC, "BZ158034" + "1GCRKSE36BZ158034");
        WriteAscii(bin, 0x8000, "BOSCH");
        Assert.Equal(BinFamilyClassifier.Family.E67,
            BinFamilyClassifier.Classify(bin));
    }

    [Fact]
    public void Classify_VinDescriptorAtE0AcWithBosch_IsE67()
    {
        // Older E67 memory map kept the VIN descriptor at 0xE0AC.
        var bin = new byte[0x100000];
        WriteAscii(bin, 0xE0AC, "BZ158034" + "1GCRKSE36BZ158034");
        WriteAscii(bin, 0x8000, "BOSCH");
        Assert.Equal(BinFamilyClassifier.Family.E67,
            BinFamilyClassifier.Classify(bin));
    }

    [Fact]
    public void Classify_VinDescriptorAtC0AcWithDelphi_IsE38()
    {
        // Delphi marker rules out E67 even when the VIN descriptor is at
        // the E67-favoured 0xC0AC offset - falls through to E38.
        var bin = new byte[0x100000];
        WriteAscii(bin, 0xC0AC, "BZ158034" + "1GCRKSE36BZ158034");
        WriteAscii(bin, 0x8000, "DELPHI");
        Assert.Equal(BinFamilyClassifier.Family.E38,
            BinFamilyClassifier.Classify(bin));
    }

    [Fact]
    public void Classify_VinDescriptorAtE0AcNoSupplierMarker_IsE38()
    {
        // Continental-supplied E38 readbacks (2010+ Silverado 6.0L LY6
        // and similar) carry neither BOSCH nor DELPHI. The classifier
        // must still recognise these as E38 from the VIN descriptor
        // alone - this was the regression that motivated the rule.
        var bin = new byte[0x100000];
        WriteAscii(bin, 0xE0AC, "BZ158034" + "1GCRKSE36BZ158034");
        Assert.Equal(BinFamilyClassifier.Family.E38,
            BinFamilyClassifier.Classify(bin));
    }

    [Fact]
    public void Classify_T43MarkerTrumpsVinDescriptor_IsT43()
    {
        // Defence-in-depth: even if a T43 image happens to contain a
        // 25-byte ASCII run at 0xC0AC (unlikely but plausible), the
        // BOSCH TC19.12 marker should win because it's the strongest
        // T43 signature we have.
        var bin = new byte[0x100000];
        WriteAscii(bin, 0x1FF80, "BOSCH TC19.12");
        WriteAscii(bin, 0xC0AC, "BZ158034" + "1GCRKSE36BZ158034");
        WriteAscii(bin, 0x8000, "BOSCH");
        Assert.Equal(BinFamilyClassifier.Family.T43,
            BinFamilyClassifier.Classify(bin));
    }

    [Fact]
    public void Name_RoundTripsEveryFamily()
    {
        Assert.Equal("T43",     BinFamilyClassifier.Name(BinFamilyClassifier.Family.T43));
        Assert.Equal("E38",     BinFamilyClassifier.Name(BinFamilyClassifier.Family.E38));
        Assert.Equal("E67",     BinFamilyClassifier.Name(BinFamilyClassifier.Family.E67));
        Assert.Equal("Unknown", BinFamilyClassifier.Name(BinFamilyClassifier.Family.Unknown));
    }

    private static void WriteAscii(byte[] bin, int offset, string s)
    {
        var bytes = Encoding.ASCII.GetBytes(s);
        Array.Copy(bytes, 0, bin, offset, bytes.Length);
    }
}
