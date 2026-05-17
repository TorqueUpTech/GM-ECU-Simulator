using Common.Protocol;
using Core.Ecu;
using Core.Identification;
using EcuSimulator.Tests.TestHelpers;
using Xunit;
using static Core.Identification.BinIdentificationReader;

namespace EcuSimulator.Tests.Ecu;

// Per-DID provenance (DidSource) tracking on EcuNode. Tells the Bin menu's
// Load Info From Bin / Auto-populate writers whether they're about to
// overwrite something the user owns.
//
// The sticky-user rule is the load-bearing invariant: once the user owns a
// row, subsequent auto-populate / merge-mode bin loads must not silently
// reclaim it. Replace-all bin loads explicitly opt out of that protection.
public class DidSourceTrackingTests
{
    [Fact]
    public void Fresh_ecu_reports_blank_for_every_did()
    {
        var node = NodeFactory.CreateNode();
        Assert.Equal(DidSource.Blank, node.GetIdentifierSource(0x90));
        Assert.Equal(DidSource.Blank, node.GetIdentifierSource(0xC1));
        Assert.Equal(DidSource.Blank, node.GetIdentifierSource(0xFF));
    }

    [Fact]
    public void SetIdentifier_records_explicit_source()
    {
        var node = NodeFactory.CreateNode();
        node.SetIdentifier(0x90, new byte[] { 1, 2, 3 }, DidSource.Bin);
        Assert.Equal(DidSource.Bin, node.GetIdentifierSource(0x90));
    }

    [Fact]
    public void SetIdentifier_single_arg_overload_defaults_to_user()
    {
        // Existing callers that don't track provenance get the User tag - the
        // safe default since most legacy call sites are direct test fixtures
        // or the row-VM user-edit path.
        var node = NodeFactory.CreateNode();
        node.SetIdentifier(0x90, new byte[] { 1, 2, 3 });
        Assert.Equal(DidSource.User, node.GetIdentifierSource(0x90));
    }

    [Fact]
    public void RemoveIdentifier_keeps_source_sticky()
    {
        // Sticky-user is the whole point: a user who deletes their entry
        // still owns the row, so a subsequent auto-populate or merge bin
        // load won't reclaim it. Same for Bin and Auto sources.
        var node = NodeFactory.CreateNode();
        node.SetIdentifier(0x90, new byte[] { 1, 2, 3 }, DidSource.User);
        node.RemoveIdentifier(0x90);
        Assert.Null(node.GetIdentifier(0x90));                  // bytes gone
        Assert.Equal(DidSource.User, node.GetIdentifierSource(0x90));  // source sticky
    }

    [Fact]
    public void ClearAllIdentifierSources_resets_every_did_to_blank()
    {
        var node = NodeFactory.CreateNode();
        node.SetIdentifier(0x90, new byte[] { 1 }, DidSource.Bin);
        node.SetIdentifier(0x92, new byte[] { 2 }, DidSource.Auto);
        node.ClearAllIdentifierSources();
        Assert.Equal(DidSource.Blank, node.GetIdentifierSource(0x90));
        Assert.Equal(DidSource.Blank, node.GetIdentifierSource(0x92));
        // Bytes are NOT cleared by this method - the caller wipes them
        // separately via RemoveIdentifier (BinIdentificationApplier does
        // both in Replace-all mode).
        Assert.NotNull(node.GetIdentifier(0x90));
    }

    [Fact]
    public void Bin_apply_merge_marks_written_dids_as_bin_only()
    {
        var node = NodeFactory.CreateNode();
        var result = FakeBinResult(vin: "BIN-VIN", supplierHwNumber: "BIN-HW");

        BinIdentificationApplier.Apply(node, result, BinIdentificationApplier.LoadMode.Merge);

        // Written DIDs: source=Bin. Untouched DIDs: still Blank.
        Assert.Equal(DidSource.Bin,   node.GetIdentifierSource(0x90));
        Assert.Equal(DidSource.Bin,   node.GetIdentifierSource(0x92));
        Assert.Equal(DidSource.Blank, node.GetIdentifierSource(0xC1));
    }

    [Fact]
    public void Bin_apply_replace_all_marks_every_known_did_as_bin_even_blank()
    {
        // The destructive opt-in: every well-known DID is now Bin source
        // regardless of whether the bin had a value for it. That's the
        // user's explicit "this ECU's identifiers are the bin's view, full
        // stop" intent.
        var node = NodeFactory.CreateNode();
        // Pre-populate one DID with the User tag and another with bytes only.
        node.SetIdentifier(0x90, new byte[] { 1, 2, 3 }, DidSource.User);
        node.SetIdentifier(0x99, new byte[] { 0x01 });          // legacy User default
        var result = FakeBinResult(vin: "BIN-VIN");

        BinIdentificationApplier.Apply(node, result, BinIdentificationApplier.LoadMode.ReplaceAll);

        // VIN written from bin -> Bin source with bytes.
        Assert.Equal("BIN-VIN", System.Text.Encoding.ASCII.GetString(node.GetIdentifier(0x90)!));
        Assert.Equal(DidSource.Bin, node.GetIdentifierSource(0x90));
        // $99 wasn't surfaced by the bin -> bytes wiped, source Bin (sticky-blank-bin).
        Assert.Null(node.GetIdentifier(0x99));
        Assert.Equal(DidSource.Bin, node.GetIdentifierSource(0x99));
        // $C1 wasn't touched before; bin didn't surface it -> Bin source, blank bytes.
        Assert.Null(node.GetIdentifier(0xC1));
        Assert.Equal(DidSource.Bin, node.GetIdentifierSource(0xC1));
    }

    private static BinIdentification FakeBinResult(string? vin = null, string? supplierHwNumber = null)
        => new(
            Family: "T43",
            ServiceDispatcherOffset: 0,
            Service1AHandlerOffset: 0,
            DidDispatcherOffset: 0,
            SupportedSids: Array.Empty<byte>(),
            Dids: Array.Empty<DidExtraction>(),
            Vin: vin,
            SupplierHardwareNumber: supplierHwNumber,
            SupplierHardwareVersion: null,
            EndModelPartNumber: null,
            BaseModelPartNumber: null,
            CalibrationPartNumber: null,
            BroadcastCode: null,
            ProgrammingDate: null,
            ProgrammingTool: null,
            TraceCode: null,
            Warnings: Array.Empty<string>());
}
