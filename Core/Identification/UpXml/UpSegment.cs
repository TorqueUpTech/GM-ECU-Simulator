namespace Core.Identification.UpXml;

// Parsed, in-memory representation of one platform's segment XML
// (e38.xml, e67.xml, e92.xml, t43.xml). Each XML file deserialises into
// an UpSegmentDefinition[] - one per <SegmentConfig> element. The fields
// here are a curated subset of UP's XML element set: only the elements
// our summary report actually consumes are exposed.
//
// Elements that exist in the XML but are intentionally not loaded:
//   CS1Address / CS2Address / CS1Method / CS2Method / CS1Blocks /
//   CS2Blocks / CS1Complement / CS2Complement / CS1SwapBytes /
//   CS2SwapBytes / Eeprom / Version / Hidden / Comment - these describe
// checksum-arithmetic configuration we don't compute (yet).
//
// Loader lives in UpXmlLoader.cs; address-mini-language parsing in
// UpAddress.cs. The two are decoupled so the language can be tested
// directly off strings without round-tripping through XML.

public sealed record UpSegmentDefinition(
    string Name,

    /// <summary>Where the segment's header lives. Usually a single
    /// <see cref="UpPointerTerm.Single"/>; sometimes a comma list of
    /// multiple pointer locations (E92 OS uses @C0126,@C012E). When the
    /// list is a single <see cref="UpPointerTerm.SearchMarker"/>, the
    /// base is discovered dynamically via SearchAddresses / Searchfor
    /// instead.</summary>
    IReadOnlyList<UpPointerTerm> Addresses,

    /// <summary>Where to read the part number from, relative to the
    /// segment base. Null when the XML element is empty (some auxiliary
    /// segments don't carry a PN).</summary>
    UpFieldAddress? PartNumberAddress,

    /// <summary>Where the printable version stamp lives (typically 2
    /// ASCII chars like "AA").</summary>
    UpFieldAddress? VersionAddress,

    /// <summary>Where the 1-byte segment number lives.</summary>
    UpFieldAddress? SegmentNumberAddress,

    /// <summary>Labelled fields visible in the report footer (Eeprom,
    /// PCM, PCMid2, VIN, trace code...). Comes from the &lt;ExtraInfo&gt;
    /// element. Empty list when not present.</summary>
    IReadOnlyList<UpExtraInfoField> ExtraInfo,

    /// <summary>Variants of a marker we use to confirm the segment base
    /// and capture named CheckWord anchors used by ExtraInfo fields.</summary>
    IReadOnlyList<UpCheckWordSpec> CheckWords,

    /// <summary>Search ranges within the bin (one or more "start-end"
    /// hex pairs from &lt;SearchAddresses&gt;). Used when Addresses is a
    /// SearchMarker placeholder.</summary>
    IReadOnlyList<(int Start, int End)> SearchAddresses,

    /// <summary>"sig:lenHex:flag" tuple parsed out of &lt;Searchfor&gt;.
    /// When present and Addresses is a SearchMarker, the base is
    /// discovered by scanning for this marker.</summary>
    UpSearchSpec? SearchSpec);

/// <summary>Parsed &lt;Searchfor&gt; payload. The hex value in the middle
/// of the XML tag (e.g. "a5a5:3c:y" -> 0x3C) is the offset of the marker
/// from the segment base: confirmed against a real E38 bin where the
/// EEPROM_DATA base 0xC000 has the A5A5 marker at 0xC03C, i.e. base+0x3C.
/// The trailing flag (typically "y") is preserved verbatim for stage 2.</summary>
public sealed record UpSearchSpec(
    ulong Marker, int MarkerSize, int MarkerOffsetFromBase, string Flag);

/// <summary>Top-level platform descriptor from &lt;family&gt;-platform.xml.</summary>
public sealed record UpPlatform(
    string Name,
    bool MsbFirst,
    string SegmentFile,
    string? TableSeekFile,
    string? SegmentSeekFile);

/// <summary>A loaded platform: descriptor + all its segments.</summary>
public sealed record UpPlatformDefinition(
    UpPlatform Platform,
    IReadOnlyList<UpSegmentDefinition> Segments);
