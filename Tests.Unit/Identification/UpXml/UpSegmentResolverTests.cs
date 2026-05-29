using Core.Identification.UpXml;
using System.Buffers.Binary;
using System.Text;
using Xunit;

namespace EcuSimulator.Tests.Identification.UpXml;

// Synthetic-bin tests for the resolver. We build minimal byte arrays
// that exercise each address grammar form and assert the resolved
// values are exactly what we put there.
public sealed class UpSegmentResolverTests
{
    [Fact]
    public void SinglePointerBase_PnVerNr_Resolve()
    {
        // Build a 64 KiB bin with:
        //   - file[0x100] = pointer to 0x4000 (segment header base)
        //   - at 0x4000+3 = SegNr = 0x07
        //   - at 0x4000+5 = PN   = 12656942 (BE u32)
        //   - at 0x4000+9 = Ver  = "AA"
        var bin = new byte[0x10000];
        BinaryPrimitives.WriteUInt32BigEndian(bin.AsSpan(0x100, 4), 0x4000);
        bin[0x4003] = 0x07;
        BinaryPrimitives.WriteUInt32BigEndian(bin.AsSpan(0x4005, 4), 12656942);
        bin[0x4009] = (byte)'A'; bin[0x400A] = (byte)'A';

        var def = new UpSegmentDefinition(
            Name: "OS",
            Addresses: new UpPointerTerm[] { new UpPointerTerm.Single(0x100) },
            PartNumberAddress: new UpFieldAddress.SegmentRelative(5, 4, UpFieldType.Int),
            VersionAddress: new UpFieldAddress.SegmentRelative(9, 2, UpFieldType.Text),
            SegmentNumberAddress: new UpFieldAddress.SegmentRelative(3, 1, UpFieldType.Int),
            ExtraInfo: Array.Empty<UpExtraInfoField>(),
            CheckWords: Array.Empty<UpCheckWordSpec>(),
            SearchAddresses: Array.Empty<(int, int)>(),
            SearchSpec: null);

        var r = new UpSegmentResolver(bin).Resolve(def);
        Assert.True(r.Found);
        Assert.Equal(0x4000, r.Base);
        Assert.Equal("12656942", r.PartNumber!.DecodedText);
        Assert.Equal("AA", r.Version!.DecodedText);
        Assert.Equal("7", r.SegmentNumber!.DecodedText);
    }

    [Fact]
    public void CheckWord_Anchor_PointsExtraInfoFieldAtRightAddress()
    {
        // Bin: pointer at 0x100 -> 0x8000 (segment base).
        // CheckWord marker A5A0 at base+0x326 (the first variant from
        // e38.xml). AnchorOffset 0x1CC. VIN bytes start at base+0x1CC.
        var bin = new byte[0x10000];
        BinaryPrimitives.WriteUInt32BigEndian(bin.AsSpan(0x100, 4), 0x8000);
        BinaryPrimitives.WriteUInt16BigEndian(bin.AsSpan(0x8000 + 0x326, 2), 0xA5A0);
        var vinBytes = Encoding.ASCII.GetBytes("6G1FK5EP6GL206970");
        Array.Copy(vinBytes, 0, bin, 0x8000 + 0x1CC, vinBytes.Length);

        var def = new UpSegmentDefinition(
            Name: "EEPROM_DATA",
            Addresses: new UpPointerTerm[] { new UpPointerTerm.Single(0x100) },
            PartNumberAddress: null,
            VersionAddress: null,
            SegmentNumberAddress: null,
            ExtraInfo: new UpExtraInfoField[]
            {
                new("VIN", new UpFieldAddress.CheckWordRelative("CWvin", 0, 17, UpFieldType.Text)),
            },
            CheckWords: new UpCheckWordSpec[]
            {
                new("CWvin", 0xA5A0, 2, 0x326, 0x1CC),
                new("CWvin", 0xA5A0, 2, 0x1DA, 0xB4),  // second variant; should be ignored
            },
            SearchAddresses: Array.Empty<(int, int)>(),
            SearchSpec: null);

        var r = new UpSegmentResolver(bin).Resolve(def);
        Assert.True(r.Found);
        Assert.Equal(0x8000 + 0x1CC, r.CheckWordAnchors["CWvin"]);
        Assert.Equal("6G1FK5EP6GL206970", r.ExtraInfo[0].DecodedText);
    }

    [Fact]
    public void CheckWord_NoMatch_FieldReturnsNullWithDiagnostic()
    {
        var bin = new byte[0x10000];
        BinaryPrimitives.WriteUInt32BigEndian(bin.AsSpan(0x100, 4), 0x8000);
        // No A5A0 marker anywhere.

        var def = new UpSegmentDefinition(
            "S",
            new UpPointerTerm[] { new UpPointerTerm.Single(0x100) },
            null, null, null,
            new UpExtraInfoField[]
            {
                new("VIN", new UpFieldAddress.CheckWordRelative("CWvin", 0, 17, UpFieldType.Text)),
            },
            new UpCheckWordSpec[]
            {
                new("CWvin", 0xA5A0, 2, 0x326, 0x1CC),
            },
            Array.Empty<(int, int)>(), null);

        var r = new UpSegmentResolver(bin).Resolve(def);
        Assert.True(r.Found);  // base resolved
        Assert.False(r.CheckWordAnchors.ContainsKey("CWvin"));
        Assert.Empty(r.ExtraInfo);
        Assert.Contains(r.Diagnostics, d => d.Contains("CWvin"));
    }

    [Fact]
    public void SearchMarker_LocatesBaseInRange()
    {
        // E38 EEPROM-shaped: scan SearchAddresses for A5A5. With
        // MarkerOffsetFromBase=0x3C and a marker placed at 0x903C, the
        // resolved base must be 0x903C-0x3C = 0x9000.
        var bin = new byte[0x10000];
        BinaryPrimitives.WriteUInt16BigEndian(bin.AsSpan(0x903C, 2), 0xA5A5);

        var def = new UpSegmentDefinition(
            "EEPROM_DATA",
            new UpPointerTerm[] { new UpPointerTerm.SearchMarker() },
            null, null, null,
            Array.Empty<UpExtraInfoField>(),
            Array.Empty<UpCheckWordSpec>(),
            new[] { (0x8000, 0x9FFF) },
            new UpSearchSpec(0xA5A5, 2, 0x3C, "y"));

        var r = new UpSegmentResolver(bin).Resolve(def);
        Assert.True(r.Found);
        Assert.Equal(0x9000, r.Base);
    }

    [Fact]
    public void OutOfRangeField_ReturnsNullWithDiagnostic()
    {
        var bin = new byte[0x100];
        BinaryPrimitives.WriteUInt32BigEndian(bin.AsSpan(0x10, 4), 0x80);
        var def = new UpSegmentDefinition(
            "S",
            new UpPointerTerm[] { new UpPointerTerm.Single(0x10) },
            new UpFieldAddress.SegmentRelative(0x100, 4, UpFieldType.Int), // 0x80+0x100=0x180 OOR
            null, null,
            Array.Empty<UpExtraInfoField>(),
            Array.Empty<UpCheckWordSpec>(),
            Array.Empty<(int, int)>(), null);

        var r = new UpSegmentResolver(bin).Resolve(def);
        Assert.True(r.Found);
        Assert.Null(r.PartNumber);
        Assert.Contains(r.Diagnostics, d => d.Contains("out of bin range"));
    }
}
