namespace Core.Identification.UpXml;

// Evaluates parsed segment definitions against a bin buffer to produce a
// BinSummary. The address language is interpreted top-down per segment:
//
//   1. Resolve the segment's base address (Addresses pointer dereference,
//      or a Searchfor scan when Addresses is "Search").
//   2. Find anchors for any named CheckWords by reading the marker at
//      base + MarkerOffset; the first CheckWord whose marker matches wins.
//   3. Resolve each field (PNAddr, VerAddr, SegNrAddr, ExtraInfo[*])
//      relative to the base or to a CheckWord anchor.
//   4. Decode bytes per FieldType (BE int / ASCII text / hex string).
//
// Anything that fails (pointer out of range, CheckWord doesn't match,
// field bytes are 0xFF padding...) is reported as a null / "(missing)"
// in the BinSummary rather than crashing the whole walk; the per-
// segment ResolutionStatus + Diagnostics fields tell the caller what
// happened.

using System.Buffers.Binary;
using System.Text;

public sealed class UpSegmentResolver
{
    readonly ReadOnlyMemory<byte> _bin;

    public UpSegmentResolver(ReadOnlyMemory<byte> bin) => _bin = bin;

    /// <summary>Resolve one segment definition. Never throws on data
    /// shape - returns a result with <see cref="UpResolvedSegment.Found"/>
    /// = false and a diagnostic instead.</summary>
    public UpResolvedSegment Resolve(UpSegmentDefinition def)
    {
        var diags = new List<string>();

        int? baseAddr = ResolveBase(def, diags);
        if (baseAddr is null)
            return new UpResolvedSegment(
                Name: def.Name, Found: false, Base: null,
                PartNumber: null, Version: null, SegmentNumber: null,
                ExtraInfo: Array.Empty<UpResolvedField>(),
                CheckWordAnchors: new Dictionary<string, int>(),
                Diagnostics: diags);

        // Resolve check-word anchors. First match per name wins.
        var anchors = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var cw in def.CheckWords)
        {
            if (anchors.ContainsKey(cw.Name)) continue;
            int markerPos = baseAddr.Value + cw.MarkerOffset;
            if (!TryReadUIntBE(markerPos, cw.MarkerSize, out var actual))
            {
                diags.Add(
                    $"CheckWord {cw.Name}: marker offset 0x{markerPos:X} out of range");
                continue;
            }
            if (actual == cw.Marker)
                anchors[cw.Name] = baseAddr.Value + cw.AnchorOffset;
        }

        var pn = ResolveOptionalField("PNAddr", def.PartNumberAddress, baseAddr.Value, anchors, diags);
        var ver = ResolveOptionalField("VerAddr", def.VersionAddress, baseAddr.Value, anchors, diags);
        var nr = ResolveOptionalField("SegNrAddr", def.SegmentNumberAddress, baseAddr.Value, anchors, diags);

        var extras = new List<UpResolvedField>(def.ExtraInfo.Count);
        foreach (var item in def.ExtraInfo)
        {
            var r = ResolveField(item.Label, item.Address, baseAddr.Value, anchors, diags);
            if (r is not null) extras.Add(r);
        }

        return new UpResolvedSegment(
            Name: def.Name, Found: true, Base: baseAddr.Value,
            PartNumber: pn, Version: ver, SegmentNumber: nr,
            ExtraInfo: extras, CheckWordAnchors: anchors,
            Diagnostics: diags);
    }

    /// <summary>Walk a whole platform, returning one resolved row per
    /// segment definition. Per-segment diagnostics are preserved.</summary>
    public IReadOnlyList<UpResolvedSegment> ResolveAll(
        UpPlatformDefinition platform)
    {
        var list = new List<UpResolvedSegment>(platform.Segments.Count);
        foreach (var s in platform.Segments) list.Add(Resolve(s));
        return list;
    }

    int? ResolveBase(UpSegmentDefinition def, List<string> diags)
    {
        if (def.Addresses.Count == 0)
        {
            diags.Add("Addresses list is empty");
            return null;
        }

        // Take the first pointer term as the canonical segment base. The
        // remaining entries (E92 OS uses "@C0126,@C012E" - a primary and
        // an alternate header pointer) are not consulted here; if the
        // primary is invalid they're a stage-2 fallback path.
        var first = def.Addresses[0];
        switch (first)
        {
            case UpPointerTerm.Single s:
                if (!TryReadUIntBE(s.FileOffset, 4, out var v))
                {
                    diags.Add($"Addresses @0x{s.FileOffset:X}: out of range");
                    return null;
                }
                return (int)v;

            case UpPointerTerm.Array a:
                if (!TryReadUIntBE(a.FileOffset, 4, out var v2))
                {
                    diags.Add($"Addresses @0x{a.FileOffset:X}*{a.Count}: out of range");
                    return null;
                }
                return (int)v2;

            case UpPointerTerm.SearchMarker:
                return ResolveBaseBySearch(def, diags);

            default:
                diags.Add("Unknown pointer term kind: " + first.GetType().Name);
                return null;
        }
    }

    int? ResolveBaseBySearch(UpSegmentDefinition def, List<string> diags)
    {
        if (def.SearchSpec is null)
        {
            diags.Add("Addresses=Search but <Searchfor> is missing");
            return null;
        }
        if (def.SearchAddresses.Count == 0)
        {
            diags.Add("Addresses=Search but <SearchAddresses> is empty");
            return null;
        }

        var spec = def.SearchSpec;
        var span = _bin.Span;
        foreach (var (start, end) in def.SearchAddresses)
        {
            int last = Math.Min(end, span.Length - spec.MarkerSize);
            for (int i = Math.Max(0, start); i <= last; i++)
            {
                if (!TryReadUIntBE(i, spec.MarkerSize, out var v)) break;
                if (v == spec.Marker)
                {
                    // Found marker at position i; segment base sits
                    // MarkerOffsetFromBase bytes earlier. Skip the match
                    // if that would put the base before the search range
                    // start - happens when the same marker bytes appear
                    // multiple times and only the later one is canonical.
                    var candidateBase = i - spec.MarkerOffsetFromBase;
                    if (candidateBase < start || candidateBase < 0) continue;
                    return candidateBase;
                }
            }
        }
        diags.Add(
            $"Searchfor marker 0x{spec.Marker:X} not found in any SearchAddresses range");
        return null;
    }

    UpResolvedField? ResolveOptionalField(
        string label, UpFieldAddress? addr, int segmentBase,
        IReadOnlyDictionary<string, int> anchors, List<string> diags)
    {
        if (addr is null) return null;
        return ResolveField(label, addr, segmentBase, anchors, diags);
    }

    UpResolvedField? ResolveField(
        string label, UpFieldAddress addr, int segmentBase,
        IReadOnlyDictionary<string, int> anchors, List<string> diags)
    {
        int abs;
        switch (addr)
        {
            case UpFieldAddress.SegmentRelative sr:
                abs = segmentBase + sr.Offset;
                break;
            case UpFieldAddress.CheckWordRelative cwr:
                if (!anchors.TryGetValue(cwr.Name, out var anchor))
                {
                    diags.Add(
                        $"Field {label}: CheckWord '{cwr.Name}' did not resolve");
                    return null;
                }
                abs = anchor + cwr.Offset;
                break;
            default:
                diags.Add($"Field {label}: unknown address kind");
                return null;
        }

        if (abs < 0 || abs + addr.Length > _bin.Length)
        {
            diags.Add(
                $"Field {label}: address 0x{abs:X}+{addr.Length} out of bin range");
            return null;
        }

        var slice = _bin.Span.Slice(abs, addr.Length).ToArray();
        return new UpResolvedField(
            Label: label,
            Address: abs,
            Bytes: slice,
            Type: addr.Type,
            DecodedText: Decode(slice, addr.Type));
    }

    static string Decode(byte[] bytes, UpFieldType type) => type switch
    {
        UpFieldType.Int => DecodeInt(bytes),
        UpFieldType.Text => DecodeText(bytes),
        UpFieldType.Hex => DecodeHex(bytes),
        _ => "?",
    };

    static string DecodeInt(byte[] bytes)
    {
        // Big-endian unsigned. 1-8 bytes covers everything UP emits.
        ulong v = 0;
        foreach (var b in bytes) v = (v << 8) | b;
        return v.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    static string DecodeText(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length);
        foreach (var b in bytes)
            sb.Append((b >= 0x20 && b < 0x7F) ? (char)b : '?');
        return sb.ToString();
    }

    static string DecodeHex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("X2"));
        return sb.ToString();
    }

    bool TryReadUIntBE(int offset, int byteCount, out ulong value)
    {
        value = 0;
        if (offset < 0 || byteCount < 1 || byteCount > 8) return false;
        if (offset + byteCount > _bin.Length) return false;
        var span = _bin.Span.Slice(offset, byteCount);
        switch (byteCount)
        {
            case 1: value = span[0]; return true;
            case 2: value = BinaryPrimitives.ReadUInt16BigEndian(span); return true;
            case 4: value = BinaryPrimitives.ReadUInt32BigEndian(span); return true;
            case 8: value = BinaryPrimitives.ReadUInt64BigEndian(span); return true;
            default:
                ulong v = 0;
                for (int i = 0; i < byteCount; i++) v = (v << 8) | span[i];
                value = v;
                return true;
        }
    }
}

/// <summary>A field after resolution: where it was read from, the raw
/// bytes, and the decoded string.</summary>
public sealed record UpResolvedField(
    string Label,
    int Address,
    byte[] Bytes,
    UpFieldType Type,
    string DecodedText);

/// <summary>One segment after resolution. Found=false means we couldn't
/// determine the base; the other fields will be null.</summary>
public sealed record UpResolvedSegment(
    string Name,
    bool Found,
    int? Base,
    UpResolvedField? PartNumber,
    UpResolvedField? Version,
    UpResolvedField? SegmentNumber,
    IReadOnlyList<UpResolvedField> ExtraInfo,
    IReadOnlyDictionary<string, int> CheckWordAnchors,
    IReadOnlyList<string> Diagnostics);
