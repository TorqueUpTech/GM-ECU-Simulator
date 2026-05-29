using Core.Identification.UpXml;
using Xunit;
using Xunit.Abstractions;

namespace EcuSimulator.Tests.Identification.UpXml;

/// <summary>
/// Temporary one-off smoke test: walk every .bin in the user's GM
/// Global A bin directory, auto-detect family from the three known
/// platforms (E38/E67/T43), and dump the formatted BinSummary so we
/// can eyeball each against UniversalPatcher's GUI output.
///
/// NOT INTENDED FOR COMMIT. The leading underscore in the filename is
/// the marker - delete this file once the smoke run is done.
/// </summary>
public sealed class _SmokeDumpAllBins
{
    readonly ITestOutputHelper _output;
    public _SmokeDumpAllBins(ITestOutputHelper output) => _output = output;

    const string BinDir =
        @"C:\Users\Nathan\OneDrive\Cars\VF2 HSV R8 LSA\GM Programming\PCMHacking\Bins\GM Global A";

    [Fact]
    public void DumpEveryBin()
    {
        if (!Directory.Exists(BinDir))
        {
            _output.WriteLine("Bin directory not found, skipping.");
            return;
        }

        // Load the autodetect ruleset + the three platform definitions
        // we care about. Map each rule's lowercased XML filename to a
        // loaded platform so the detector's output (e.g. "e67.xml")
        // resolves to the right segment layout.
        var rules = UpAutoDetector.Load(LocateVendoredXml("autodetect.xml"));
        var platforms = new Dictionary<string, UpPlatformDefinition>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["e38.xml"] = UpXmlLoader.LoadPlatformWithSegments(
                LocateVendoredXml("e38-platform.xml")),
            ["e67.xml"] = UpXmlLoader.LoadPlatformWithSegments(
                LocateVendoredXml("e67-platform.xml")),
            ["t43.xml"] = UpXmlLoader.LoadPlatformWithSegments(
                LocateVendoredXml("t43-platform.xml")),
            ["t43-0.xml"] = UpXmlLoader.LoadPlatformWithSegments(
                LocateVendoredXml("t43-0-platform.xml")),
        };

        var bins = Directory.GetFiles(BinDir, "*.bin")
            .OrderBy(p => Path.GetFileName(p)).ToArray();

        foreach (var binPath in bins)
        {
            var name = Path.GetFileName(binPath);
            var bin = File.ReadAllBytes(binPath);

            var detectedHits = UpAutoDetector.Detect(rules, bin);
            // Restrict pick to platforms we have an XML loaded for; this
            // suppresses generic l4-98 / gm-os-type41-style matches in
            // favour of the GM Global-A platforms (e38/e67/t43/t43-0) we
            // actually care about. Within that restricted set, take the
            // detector's "most specific" choice.
            var rulesForKnown = rules
                .Where(r => platforms.ContainsKey(r.Xml))
                .ToList();
            var picked = UpAutoDetector.PickMostSpecific(rulesForKnown, bin);

            _output.WriteLine("==============================================");
            _output.WriteLine($"FILE   : {name}");
            _output.WriteLine($"SIZE   : 0x{bin.Length:X} ({bin.Length:N0} bytes)");
            _output.WriteLine(
                $"DETECT : hits=[{string.Join(", ", detectedHits)}]  picked={picked ?? "(none)"}");

            if (picked is null || !platforms.TryGetValue(picked, out var platform))
            {
                _output.WriteLine(
                    "FAMILY : (autodetect rejected, or platform XML not vendored)");
                _output.WriteLine("");
                continue;
            }

            var summary = BinSummaryReporter.Build(platform, bin);
            _output.WriteLine($"FAMILY : {platform.Platform.Name}");
            _output.WriteLine("");
            _output.WriteLine(BinSummaryReporter.Format(summary));
            foreach (var d in summary.Diagnostics)
                _output.WriteLine("  diag: " + d);
            _output.WriteLine("");
        }
    }

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
            "Could not locate Resources/UniversalPatcher/XML/" + fileName);
    }
}
