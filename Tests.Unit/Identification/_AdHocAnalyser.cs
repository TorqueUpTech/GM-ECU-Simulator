using Core.Identification;
using Xunit;
using Xunit.Abstractions;

namespace EcuSimulator.Tests.Identification;

// Ad-hoc analyser - emits parser output for a batch of user-supplied bins
// to stdout via xUnit's ITestOutputHelper. Deletable after the user has
// triaged the report; nothing else in the suite depends on it.
public sealed class _AdHocAnalyser
{
    private readonly ITestOutputHelper output;
    public _AdHocAnalyser(ITestOutputHelper output) => this.output = output;

    private static readonly (string label, string path)[] Bins =
    {
        ("E38 2010 12633238",    @"C:\Users\Nathan\OneDrive\Cars\VF2 HSV R8 LSA\GM Programming\PCMHacking\Bins\GM Global A\E38_2010_12633238.bin"),
        ("E38 2013 Silv LC9",    @"C:\Users\Nathan\OneDrive\Cars\VF2 HSV R8 LSA\GM Programming\PCMHacking\Bins\GM Global A\E38_2013 Chev Silverado_LC9 Flex Fuel.bin"),
        ("E38 12639270 Tre-cool",@"C:\Users\Nathan\OneDrive\Cars\VF2 HSV R8 LSA\GM Programming\PCMHacking\Bins\GM Global A\GM E38-12639270-Auto.bin"),
        ("E67 2009 CTS-V",       @"C:\Users\Nathan\OneDrive\Cars\VF2 HSV R8 LSA\GM Programming\PCMHacking\Bins\GM Global A\E67 2009 Cadillac CTSV E67 - 12632176.bin"),
        ("E67 2016 HSV LSA",     @"C:\Users\Nathan\OneDrive\Cars\VF2 HSV R8 LSA\GM Programming\PCMHacking\Ghidra IDA\ECM\12656942_Mine\2016 HSV R8 LSA 12656942.bin"),
        ("T43 Bens Stock",       @"C:\Users\Nathan\OneDrive\Cars\VF2 HSV R8 LSA\GM Programming\PCMHacking\Bins\GM Global A\Bens_Stock Read_T43.bin"),
    };

    [Fact]
    public void EmitReportForUserSuppliedBins()
    {
        foreach (var (label, path) in Bins)
        {
            output.WriteLine("");
            output.WriteLine(new string('=', 78));
            output.WriteLine($"# {label}");
            output.WriteLine($"# {path}");
            output.WriteLine(new string('=', 78));

            if (!File.Exists(path)) { output.WriteLine("  (file missing)"); continue; }
            var bytes = File.ReadAllBytes(path);
            output.WriteLine($"  size: {bytes.Length:N0} bytes (0x{bytes.Length:X})");

            var r = Mode1ADidBinExtractor.Parse(bytes);
            if (r == null) { output.WriteLine("  Parse returned null - no dispatcher found."); continue; }

            output.WriteLine($"  family            : {r.Family}");
            output.WriteLine($"  service dispatcher: 0x{r.ServiceDispatcherOffset:X6}");
            output.WriteLine($"  $1A handler       : 0x{r.Service1AHandlerOffset:X6}");
            output.WriteLine($"  DID dispatcher    : 0x{r.DidDispatcherOffset:X6}");
            output.WriteLine($"  Supported SIDs    : {string.Join(", ", r.SupportedSids.Select(s => $"${s:X2}"))}");
            output.WriteLine($"  Supported DIDs    : {string.Join(", ", r.Dids.Select(d => $"${d.Did:X2}"))}");
            foreach (var did in r.Dids)
            {
                var hex = did.WireBytes.Length > 0 ? BitConverter.ToString(did.WireBytes).Replace("-", " ") : "(none)";
                var addr = did.FlashAddress is null ? "n/a" : $"0x{did.FlashAddress:X6}";
                output.WriteLine($"    ${did.Did:X2}: kind={did.Kind,-18} addr={addr,-10} bytes={hex,-13} decoded={did.DecodedValue}");
            }
            output.WriteLine($"  VIN               : {r.Vin ?? "(not found)"}");
            output.WriteLine($"  $92 supplier HW   : {r.SupplierHardwareNumber ?? "(not found)"}");
            output.WriteLine($"  $98 supplier ver  : {r.SupplierHardwareVersion ?? "(not found)"}");
            output.WriteLine($"  $C1 end PN        : {r.EndModelPartNumber ?? "(not found)"}");
            output.WriteLine($"  $C2 base PN       : {r.BaseModelPartNumber ?? "(not found)"}");
            if (r.Warnings.Count > 0)
            {
                output.WriteLine("  warnings:");
                foreach (var w in r.Warnings) output.WriteLine("    - " + w);
            }
        }
    }
}
