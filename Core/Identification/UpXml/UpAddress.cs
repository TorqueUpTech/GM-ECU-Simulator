namespace Core.Identification.UpXml;

// Clean-room parser for the address mini-language used inside
// UniversalPatcher's segment XML files (e38.xml, e67.xml, e92.xml,
// t43.xml, ...). The XML files are vendored at
// Resources/UniversalPatcher/XML/ under GPLv3 (see NOTICE.md alongside);
// this parser was written by inspecting the data only, without consulting
// UP's C# source.
//
// The grammar covers the subset of UP's address language that we need to
// produce the per-segment summary block:
//
//   "Pointer term"   (used in <Addresses>, <CS1Blocks>, <CS2Blocks>):
//     @HEX          -> read 4-byte BE pointer at file offset HEX
//     @HEX*N        -> read N consecutive 4-byte BE pointers starting at HEX
//     Search        -> base is discovered dynamically via <Searchfor>
//     a, b, c       -> comma-separated list of any of the above
//
//   "Field term"     (used in <PNAddr>, <VerAddr>, <SegNrAddr>, <ExtraInfo>):
//     #HEX:LEN:TYPE         -> at segment_base + HEX, read LEN bytes as TYPE
//     CWNAME+DEC:LEN:TYPE   -> at CheckWord(name=CWNAME).Anchor + DEC, ditto
//   where TYPE is "int" (big-endian unsigned), "text" (ASCII) or "hex".
//
//   "CheckWord term" (used in <CheckWords>):
//     MARKER_HEX:MARKEROFFSET_HEX:ANCHOROFFSET_HEX:NAME
//
// Numeric literals are case-insensitive hex with no 0x prefix; the LEN /
// DEC fragments after a ':' are decimal. The parser silently trims
// whitespace around list items. Unrecognised forms throw
// UpAddressParseException with the original input verbatim so the caller
// can attribute the failure to a specific XML file/element.

using System.Globalization;

public sealed class UpAddressParseException(string message, string input)
    : FormatException(message + "  (input: \"" + input + "\")")
{
    public string Input { get; } = input;
}

public enum UpFieldType { Int, Text, Hex }

/// <summary>One element of a pointer list inside an Addresses/CS*Blocks tag.</summary>
public abstract record UpPointerTerm
{
    /// <summary>"@HEX" - one pointer.</summary>
    public sealed record Single(int FileOffset) : UpPointerTerm;
    /// <summary>"@HEX*N" - N consecutive pointers.</summary>
    public sealed record Array(int FileOffset, int Count) : UpPointerTerm;
    /// <summary>"Search" - base discovered via Searchfor.</summary>
    public sealed record SearchMarker : UpPointerTerm;
}

/// <summary>A field address expression (PNAddr / VerAddr / ExtraInfo item).</summary>
public abstract record UpFieldAddress(int Length, UpFieldType Type)
{
    /// <summary>"#HEX:LEN:TYPE" - at segment_base + HEX.</summary>
    public sealed record SegmentRelative(int Offset, int Len, UpFieldType T)
        : UpFieldAddress(Len, T);
    /// <summary>"NAME+DEC:LEN:TYPE" - at named CheckWord anchor + DEC.</summary>
    public sealed record CheckWordRelative(string Name, int Offset, int Len, UpFieldType T)
        : UpFieldAddress(Len, T);
}

/// <summary>One entry from a CheckWords list.</summary>
public sealed record UpCheckWordSpec(
    string Name,
    ulong Marker,
    int MarkerSize,
    int MarkerOffset,
    int AnchorOffset);

/// <summary>One labelled field from an ExtraInfo string.</summary>
public sealed record UpExtraInfoField(string Label, UpFieldAddress Address);

public static class UpAddressParser
{
    /// <summary>
    /// Parse an Addresses / CS1Blocks / CS2Blocks value. Returns one term
    /// per comma-separated entry. Empty / whitespace input returns an
    /// empty list. The placeholder string "Search" produces a single
    /// SearchMarker term.
    /// </summary>
    public static IReadOnlyList<UpPointerTerm> ParsePointerList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<UpPointerTerm>();
        var text = raw.Trim();
        if (text.Equals("Search", StringComparison.OrdinalIgnoreCase))
            return new UpPointerTerm[] { new UpPointerTerm.SearchMarker() };

        var items = new List<UpPointerTerm>();
        foreach (var piece in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
            items.Add(ParseOnePointer(piece.Trim(), raw));
        return items;
    }

    static UpPointerTerm ParseOnePointer(string piece, string original)
    {
        if (!piece.StartsWith('@'))
            throw new UpAddressParseException(
                "Pointer term must start with '@'", original);
        var body = piece.Substring(1);

        int star = body.IndexOf('*');
        if (star < 0)
            return new UpPointerTerm.Single(ParseHexInt(body, original));

        var off = ParseHexInt(body.Substring(0, star), original);
        var n = ParseDecInt(body.Substring(star + 1), original);
        if (n <= 0)
            throw new UpAddressParseException("Pointer-array count must be > 0", original);
        return new UpPointerTerm.Array(off, n);
    }

    /// <summary>
    /// Parse a single field address. Throws on empty input - field
    /// addresses are required where they appear in the XML schema.
    /// </summary>
    public static UpFieldAddress ParseFieldAddress(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new UpAddressParseException("Field address is empty", raw ?? "");
        var text = raw.Trim();

        // segments split by ':'
        var parts = text.Split(':');
        if (parts.Length != 3)
            throw new UpAddressParseException(
                "Field address must have form OFFSET:LEN:TYPE", raw);
        var lenStr = parts[1].Trim();
        var typeStr = parts[2].Trim();
        var len = ParseDecInt(lenStr, raw);
        var type = ParseFieldType(typeStr, raw);

        var addrPart = parts[0].Trim();
        if (addrPart.StartsWith('#'))
        {
            var off = ParseHexInt(addrPart.Substring(1), raw);
            return new UpFieldAddress.SegmentRelative(off, len, type);
        }

        int plus = addrPart.IndexOf('+');
        if (plus < 0)
            throw new UpAddressParseException(
                "Field address must be '#HEX' or 'NAME+DEC'", raw);
        var name = addrPart.Substring(0, plus).Trim();
        var off2 = ParseDecInt(addrPart.Substring(plus + 1), raw);
        if (name.Length == 0)
            throw new UpAddressParseException("CheckWord name is empty", raw);
        return new UpFieldAddress.CheckWordRelative(name, off2, len, type);
    }

    /// <summary>
    /// Try to parse an ExtraInfo string. The format is a comma-separated
    /// list of "LABEL:FIELDADDRESS" items where FIELDADDRESS itself
    /// contains two ':' characters - so we re-join the 1st, 2nd and 3rd
    /// trailing colons before parsing the field.
    /// </summary>
    public static IReadOnlyList<UpExtraInfoField> ParseExtraInfo(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<UpExtraInfoField>();
        var items = new List<UpExtraInfoField>();
        foreach (var piece in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = piece.Trim();
            // Each item is "LABEL:FIELDSPEC" where FIELDSPEC has the
            // form "#HEX:LEN:TYPE" or "NAME+DEC:LEN:TYPE" - 3 colon-
            // separated segments. So total = 4 segments; the label is
            // segment 0 and the field spec is segments 1..3 joined.
            var parts = p.Split(':');
            if (parts.Length < 4)
                throw new UpAddressParseException(
                    "ExtraInfo item must be LABEL:FIELDADDR (4 colon segments)", piece);
            var label = parts[0].Trim();
            var fieldRaw = string.Join(':', parts, 1, parts.Length - 1);
            items.Add(new UpExtraInfoField(label, ParseFieldAddress(fieldRaw)));
        }
        return items;
    }

    /// <summary>
    /// Parse a CheckWords string. Each comma-separated entry has form
    /// "MARKER_HEX:MARKEROFFSET_HEX:ANCHOROFFSET_HEX:NAME". The marker
    /// byte-width is inferred from the literal length (1-8 bytes).
    /// </summary>
    public static IReadOnlyList<UpCheckWordSpec> ParseCheckWords(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<UpCheckWordSpec>();
        var items = new List<UpCheckWordSpec>();
        foreach (var piece in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = piece.Trim();
            var parts = p.Split(':');
            if (parts.Length != 4)
                throw new UpAddressParseException(
                    "CheckWord must have form MARKER:MARKEROFFSET:ANCHOROFFSET:NAME", piece);
            var markerStr = parts[0].Trim();
            var marker = ulong.Parse(
                markerStr, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var markerSize = (markerStr.Length + 1) / 2;
            if (markerSize < 1 || markerSize > 8)
                throw new UpAddressParseException(
                    "CheckWord marker must be 1..8 bytes wide", piece);
            var markerOff = ParseHexInt(parts[1].Trim(), piece);
            var anchorOff = ParseHexInt(parts[2].Trim(), piece);
            var name = parts[3].Trim();
            if (name.Length == 0)
                throw new UpAddressParseException("CheckWord name is empty", piece);
            items.Add(new UpCheckWordSpec(name, marker, markerSize, markerOff, anchorOff));
        }
        return items;
    }

    static int ParseHexInt(string s, string original)
    {
        if (!int.TryParse(
                s.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
            throw new UpAddressParseException("Cannot parse hex integer '" + s + "'", original);
        return v;
    }

    static int ParseDecInt(string s, string original)
    {
        if (!int.TryParse(
                s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            throw new UpAddressParseException("Cannot parse decimal integer '" + s + "'", original);
        return v;
    }

    static UpFieldType ParseFieldType(string s, string original) => s.ToLowerInvariant() switch
    {
        "int" => UpFieldType.Int,
        "text" => UpFieldType.Text,
        "hex" => UpFieldType.Hex,
        _ => throw new UpAddressParseException("Unknown field type '" + s + "'", original),
    };
}
