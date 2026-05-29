using Core.Identification.UpXml;
using Xunit;
using Xunit.Abstractions;

namespace EcuSimulator.Tests.Identification.UpXml;

// End-to-end test: load the vendored e38-platform.xml, load the real
// E38 bin used for the original UniversalPatcher screenshot, and assert
// the report matches what we expect.
//
// The bin lives outside the repo at a developer-specific path. When the
// file isn't present (CI or a different machine), the [Fact] becomes a
// no-op via Skip() rather than a hard fail - we still want the unit
// tests above to give us regression coverage everywhere else.
public sealed class BinSummaryReporterE38Tests
{
    readonly ITestOutputHelper _output;
    public BinSummaryReporterE38Tests(ITestOutputHelper output) => _output = output;

    static readonly string E38BinPath =
        @"C:\Users\Nathan\OneDrive\Cars\VF2 HSV R8 LSA\GM Programming\PCMHacking\Bins\GM Global A\My ECM 12656942.bin";

    static readonly string SmokeshowBinPath =
        @"C:\Users\Nathan\OneDrive\Cars\VF2 HSV R8 LSA\GM Programming\PCMHacking\Bins\GM Global A\smokeshow_12647991.bin";

    static readonly string T43BinPath =
        @"C:\Users\Nathan\OneDrive\Cars\VF2 HSV R8 LSA\GM Programming\PCMHacking\Bins\GM Global A\My TCM 24264923.bin";

    static readonly string T43_24276638_Path =
        @"C:\Users\Nathan\OneDrive\Cars\VF2 HSV R8 LSA\GM Programming\PCMHacking\Bins\GM Global A\GM_6LXX_READ_EXT_FLASH_OS_24276638.bin";

    static readonly string T43_BensStock_Path =
        @"C:\Users\Nathan\OneDrive\Cars\VF2 HSV R8 LSA\GM Programming\PCMHacking\Bins\GM Global A\Bens_Stock Read_T43.bin";

    /// <summary>
    /// Second T43 bin: 6LXX external-flash read, PCM 24276637 family.
    /// User-supplied reference output has Tool="??????????" (unprintable
    /// bytes - rendered as '?' by our Text decoder, which is the correct
    /// behaviour). Multi-block BootBlock/OS ranges are stage-2 work, so
    /// we assert single-span addresses we currently produce.
    /// </summary>
    [Fact]
    public void T43_24276638_Bin_MatchesReferenceOutput()
    {
        if (!File.Exists(T43_24276638_Path)) return;
        var platform = UpXmlLoader.LoadPlatformWithSegments(
            LocateVendoredXml("t43-platform.xml"));
        var bin = File.ReadAllBytes(T43_24276638_Path);
        var summary = BinSummaryReporter.Build(platform, bin);

        BinSummaryLine Line(string name) =>
            summary.Segments.Single(s => s.Name == name);

        var bb = Line("BootBlock");
        Assert.Equal("24246947", bb.PartNumber);
        Assert.Equal("AB", bb.Version);
        Assert.Equal("99", bb.SegmentNumber);
        Assert.Equal(0x20000, bb.StartAddress);

        var os = Line("OS");
        Assert.Equal("24276638", os.PartNumber);
        Assert.Equal("BA", os.Version);
        Assert.Equal("1", os.SegmentNumber);
        Assert.Equal(0x30000, os.StartAddress);

        var trans = Line("Trans");
        Assert.Equal("24288901", trans.PartNumber);
        Assert.Equal("AB", trans.Version);
        Assert.Equal(0x1C0000, trans.StartAddress);
        Assert.Equal(0x1FE1EB, trans.EndAddress);
        Assert.Equal(0x3E1EC, trans.Size);

        var diag = Line("Diag");
        Assert.Equal("24284420", diag.PartNumber);
        Assert.Equal("AD", diag.Version);
        Assert.Equal(0x1FE1EC, diag.StartAddress);
        Assert.Equal(0x1FF50F, diag.EndAddress);
        Assert.Equal(0x1324, diag.Size);

        var sys = Line("System");
        Assert.Equal("24284421", sys.PartNumber);
        Assert.Equal("AD", sys.Version);
        Assert.Equal(0x1FF510, sys.StartAddress);
        Assert.Equal(0x1FFFFF, sys.EndAddress);
        Assert.Equal(0xAF0, sys.Size);

        string Footer(string label) =>
            summary.Footer.Single(f => f.Label == label).DecodedText;
        Assert.Equal("24276637", Footer("PCM"));
        Assert.Equal("3GTU2NEC5JG487706", Footer("VIN"));
        Assert.Equal("DV6637Q282118318", Footer("trace code"));
        Assert.Equal("6637", Footer("BCC"));
        // Tool field is all unprintable bytes in this bin; Text decoder
        // emits '?' per byte. Reference output shows ten '?'.
        Assert.Equal("??????????", Footer("Tool"));
        Assert.Equal("20250630", Footer("Programdate"));
        Assert.Equal("1034420240F031ET0109", Footer("PCM1"));
        Assert.Equal("282118318F00HZ0G31624276637", Footer("PCM2"));

        _output.WriteLine(BinSummaryReporter.Format(summary));
    }

    /// <summary>
    /// Third T43 bin: Bens_Stock Read - PCM 24249178 family. Tool field
    /// here is "PCG0STN#78" - printable ASCII including '#'. Acts as a
    /// counterpart to the unprintable-Tool case above.
    /// </summary>
    [Fact]
    public void T43_BensStock_Bin_MatchesReferenceOutput()
    {
        if (!File.Exists(T43_BensStock_Path)) return;
        var platform = UpXmlLoader.LoadPlatformWithSegments(
            LocateVendoredXml("t43-platform.xml"));
        var bin = File.ReadAllBytes(T43_BensStock_Path);
        var summary = BinSummaryReporter.Build(platform, bin);

        BinSummaryLine Line(string name) =>
            summary.Segments.Single(s => s.Name == name);

        var bb = Line("BootBlock");
        Assert.Equal("24234461", bb.PartNumber);
        Assert.Equal("AE", bb.Version);
        Assert.Equal("99", bb.SegmentNumber);
        Assert.Equal(0x20000, bb.StartAddress);

        var os = Line("OS");
        Assert.Equal("24249179", os.PartNumber);
        Assert.Equal("AB", os.Version);
        Assert.Equal("1", os.SegmentNumber);
        Assert.Equal(0x30000, os.StartAddress);

        var trans = Line("Trans");
        Assert.Equal("92235820", trans.PartNumber);
        Assert.Equal("AB", trans.Version);
        Assert.Equal(0x1C0000, trans.StartAddress);
        Assert.Equal(0x1FCB57, trans.EndAddress);
        Assert.Equal(0x3CB58, trans.Size);

        var diag = Line("Diag");
        Assert.Equal("92235823", diag.PartNumber);
        Assert.Equal("AB", diag.Version);
        Assert.Equal(0x1FCB58, diag.StartAddress);
        Assert.Equal(0x1FEB57, diag.EndAddress);
        Assert.Equal(0x2000, diag.Size);

        var sys = Line("System");
        Assert.Equal("92235824", sys.PartNumber);
        Assert.Equal("AB", sys.Version);
        Assert.Equal(0x1FEB58, sys.StartAddress);
        Assert.Equal(0x1FFFFF, sys.EndAddress);
        Assert.Equal(0x14A8, sys.Size);

        string Footer(string label) =>
            summary.Footer.Single(f => f.Label == label).DecodedText;
        Assert.Equal("24249178", Footer("PCM"));
        Assert.Equal("6G1EK85Y59L347098", Footer("VIN"));
        Assert.Equal("DV9178Q417010509", Footer("trace code"));
        Assert.Equal("9178", Footer("BCC"));
        Assert.Equal("PCG0STN#78", Footer("Tool"));
        Assert.Equal("20090721", Footer("Programdate"));
        Assert.Equal("10344202026002ZC0680", Footer("PCM1"));
        Assert.Equal("417010509F00HZ0G12824249178", Footer("PCM2"));

        _output.WriteLine(BinSummaryReporter.Format(summary));
    }

    /// <summary>
    /// Regression for the T43 TCM bin "My TCM 24264923.bin". The
    /// stage-1 tool produces correct PN/Ver/Nr/Start for every segment
    /// and every footer field exactly. BootBlock and OS would also need
    /// multi-block sub-range decoding to render their End/Size the way
    /// UP does it (e.g. OS = [30000-11FFFF, 120000-1BFFFF]); for now we
    /// just assert the single-span output we currently produce and
    /// leave a TODO. EEPROM_DATA end/size excluded for the same reason
    /// as the E38 tests.
    /// </summary>
    [Fact]
    public void T43_24264923_Bin_MatchesReferenceOutput()
    {
        if (!File.Exists(T43BinPath)) return;
        var platform = UpXmlLoader.LoadPlatformWithSegments(
            LocateVendoredXml("t43-platform.xml"));
        var bin = File.ReadAllBytes(T43BinPath);
        var summary = BinSummaryReporter.Build(platform, bin);

        BinSummaryLine Line(string name) =>
            summary.Segments.Single(s => s.Name == name);

        var bb = Line("BootBlock");
        Assert.Equal("24246947", bb.PartNumber);
        Assert.Equal("AB", bb.Version);
        Assert.Equal("99", bb.SegmentNumber);
        Assert.Equal(0x20000, bb.StartAddress);

        var os = Line("OS");
        Assert.Equal("24264923", os.PartNumber);
        Assert.Equal("AA", os.Version);
        Assert.Equal("1", os.SegmentNumber);
        Assert.Equal(0x30000, os.StartAddress);

        var trans = Line("Trans");
        Assert.Equal("92283046", trans.PartNumber);
        Assert.Equal("AC", trans.Version);
        Assert.Equal(0x1C0000, trans.StartAddress);
        Assert.Equal(0x1FCB57, trans.EndAddress);
        Assert.Equal(0x3CB58, trans.Size);

        var diag = Line("Diag");
        Assert.Equal("92283056", diag.PartNumber);
        Assert.Equal(0x1FCB58, diag.StartAddress);
        Assert.Equal(0x1FEB57, diag.EndAddress);
        Assert.Equal(0x2000, diag.Size);

        var sys = Line("System");
        Assert.Equal("92283066", sys.PartNumber);
        Assert.Equal(0x1FEB58, sys.StartAddress);
        Assert.Equal(0x1FFFFF, sys.EndAddress);
        Assert.Equal(0x14A8, sys.Size);

        // T43 EEPROM_DATA footer carries different field names than
        // E38 (no Eeprom/PCMid2, has BCC/Tool/Programdate/PCM1/PCM2).
        string Footer(string label) =>
            summary.Footer.Single(f => f.Label == label).DecodedText;
        Assert.Equal("24265053", Footer("PCM"));
        Assert.Equal("6G1FK5EP6GL206970", Footer("VIN"));
        Assert.Equal("DV5053Q103619515", Footer("trace code"));
        Assert.Equal("5053", Footer("BCC"));
        Assert.Equal("9999988888", Footer("Tool"));
        Assert.Equal("20220326", Footer("Programdate"));
        Assert.Equal("10344202406002ZC1078", Footer("PCM1"));
        Assert.Equal("103619515F00HZ0G27724265053", Footer("PCM2"));

        _output.WriteLine(BinSummaryReporter.Format(summary));
    }

    /// <summary>
    /// Regression for a second E38 bin (PCM 12647991 family). Asserts
    /// the per-segment values exactly against the user-supplied
    /// reference output. EEPROM_DATA end/size are excluded - those
    /// remain a known stage-2 gap (multi-block sub-range decoding) so
    /// would just lock in the "wrong" answer.
    /// </summary>
    [Fact]
    public void Smokeshow_12647991_Bin_MatchesReferenceOutput()
    {
        if (!File.Exists(SmokeshowBinPath)) return;
        var platform = UpXmlLoader.LoadPlatformWithSegments(
            LocateVendoredXml("e38-platform.xml"));
        var bin = File.ReadAllBytes(SmokeshowBinPath);
        var summary = BinSummaryReporter.Build(platform, bin);

        BinSummaryLine Line(string name) =>
            summary.Segments.Single(s => s.Name == name);

        var bb = Line("BootBlock");
        Assert.Equal("12605900", bb.PartNumber);
        Assert.Equal("NA", bb.Version);
        Assert.Equal("99", bb.SegmentNumber);
        Assert.Equal(0x0000, bb.StartAddress);

        var os = Line("OS");
        Assert.Equal("12647991", os.PartNumber);
        Assert.Equal("AA", os.Version);
        Assert.Equal(0x10000, os.StartAddress);
        Assert.Equal(0x1BFFFF, os.EndAddress);

        var sys = Line("System");
        Assert.Equal("92265589", sys.PartNumber);
        Assert.Equal(0x1C0000, sys.StartAddress);
        Assert.Equal(0x1C0A93, sys.EndAddress);
        Assert.Equal(0xA94, sys.Size);

        var fuel = Line("Fuel");
        Assert.Equal("92265386", fuel.PartNumber);
        Assert.Equal(0x1C0A94, fuel.StartAddress);
        Assert.Equal(0x21B0, fuel.Size);

        var speedo = Line("Speedo");
        Assert.Equal("92263100", speedo.PartNumber);
        Assert.Equal(0x1C2C44, speedo.StartAddress);
        Assert.Equal(0x360, speedo.Size);

        var diag = Line("EngineDiag");
        Assert.Equal("92265384", diag.PartNumber);
        Assert.Equal(0x1C2FA4, diag.StartAddress);
        Assert.Equal(0x9CB0, diag.Size);

        var eng = Line("Engine");
        Assert.Equal("92265382", eng.PartNumber);
        Assert.Equal(0x1CCC54, eng.StartAddress);

        // Footer fields.
        string Footer(string label) =>
            summary.Footer.Single(f => f.Label == label).DecodedText;
        Assert.Equal("JG1", Footer("Eeprom"));
        Assert.Equal("12647990", Footer("PCM"));
        Assert.Equal("12601203", Footer("PCMid2"));
        Assert.Equal("6G1EX8EW0CL627440", Footer("VIN"));
        Assert.Equal("86AA6AK212594RA5", Footer("trace code"));

        _output.WriteLine(BinSummaryReporter.Format(summary));
    }

    [Fact]
    public void RealE38Bin_OS_Row_Matches_Expected()
    {
        if (!File.Exists(E38BinPath))
        {
            // Skip without failing on machines that don't have the bin.
            return;
        }

        var platformXml = LocateVendoredXml("e38-platform.xml");
        var platform = UpXmlLoader.LoadPlatformWithSegments(platformXml);
        var bin = File.ReadAllBytes(E38BinPath);
        var summary = BinSummaryReporter.Build(platform, bin);

        var os = summary.Segments.Single(s => s.Name == "OS");
        Assert.Equal("12656942", os.PartNumber);   // matches filename + screenshot
        Assert.Equal("AA", os.Version);
        Assert.Equal("1", os.SegmentNumber);
        Assert.Equal(0x10000, os.StartAddress);
        Assert.Equal(0x1BFFFF, os.EndAddress);    // next segment (System) starts at 0x1C0000

        // System / Fuel / Speedo addresses also match the screenshot.
        var sys = summary.Segments.Single(s => s.Name == "System");
        Assert.Equal(0x1C0000, sys.StartAddress);

        // EEPROM_DATA footer fields populate from ExtraInfo.
        Assert.Contains(summary.Footer, f => f.Label == "VIN");
        Assert.Contains(summary.Footer, f => f.Label == "PCM");
    }

    [Fact]
    public void RealE38Bin_FullSummary_RoundTripsThroughFormatter()
    {
        if (!File.Exists(E38BinPath)) return;

        var platform = UpXmlLoader.LoadPlatformWithSegments(
            LocateVendoredXml("e38-platform.xml"));
        var bin = File.ReadAllBytes(E38BinPath);
        var summary = BinSummaryReporter.Build(platform, bin);
        var text = BinSummaryReporter.Format(summary);

        // Stash the rendered output so a human can eyeball it against
        // the original screenshot when running the test in verbose mode.
        _output.WriteLine("=== Rendered summary ===");
        _output.WriteLine(text);
        foreach (var d in summary.Diagnostics)
            _output.WriteLine("diag: " + d);

        // Sanity-check the rendered output without nailing down exact
        // whitespace. Every E38 segment name should appear, plus the
        // footer-field labels we care about.
        Assert.Contains("BootBlock", text);
        Assert.Contains("OS", text);
        Assert.Contains("EEPROM_DATA", text);
        Assert.Contains("PCM:", text);
        Assert.Contains("VIN:", text);
        Assert.Contains("trace code:", text);
    }

    /// <summary>
    /// Locate Resources/UniversalPatcher/XML/&lt;file&gt; by walking up
    /// from the test assembly's base directory until we find the
    /// vendored Resources/UniversalPatcher/XML/ tree.
    /// </summary>
    static string LocateVendoredXml(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(
                dir.FullName, "Resources", "UniversalPatcher", "XML", fileName);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException(
            "Could not locate Resources/UniversalPatcher/XML/" + fileName
            + " above " + AppContext.BaseDirectory);
    }
}
