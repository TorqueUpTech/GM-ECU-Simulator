namespace Core.Identification.UpXml;

// XDocument-based loader for the vendored UniversalPatcher XML files.
// Deliberately handwritten rather than using XmlSerializer with classes
// that would have to ape UP's source-side type names - we read only the
// element names we need, by their exact spelling. Element names are the
// data-format contract (the bits on disk) so matching them is fine
// regardless of clean-room boundary.

using System.Globalization;
using System.Xml.Linq;

public static class UpXmlLoader
{
    /// <summary>
    /// Load &lt;family&gt;-platform.xml. Throws if the file isn't a
    /// PcmPlatform document. The returned descriptor's <see
    /// cref="UpPlatform.Name"/> is derived from the file's stem
    /// (e.g. "e38" for "e38-platform.xml").
    /// </summary>
    public static UpPlatform LoadPlatform(string path)
    {
        var doc = XDocument.Load(path);
        var root = doc.Root
            ?? throw new InvalidDataException("Platform XML has no root: " + path);
        if (root.Name.LocalName != "PcmPlatform")
            throw new InvalidDataException(
                "Expected <PcmPlatform>, got <" + root.Name.LocalName + ">: " + path);

        var name = DerivePlatformName(path);
        return new UpPlatform(
            Name: name,
            MsbFirst: BoolOrDefault(root.Element("MSB"), defaultValue: true),
            SegmentFile: RequiredText(root, "SegmentFile", path),
            TableSeekFile: OptionalText(root, "TableSeekFile"),
            SegmentSeekFile: OptionalText(root, "SegmentSeekFile"));
    }

    /// <summary>
    /// Load &lt;family&gt;.xml - the <c>ArrayOfSegmentConfig</c> document.
    /// One <see cref="UpSegmentDefinition"/> per &lt;SegmentConfig&gt;.
    /// </summary>
    public static IReadOnlyList<UpSegmentDefinition> LoadSegments(string path)
    {
        var doc = XDocument.Load(path);
        var root = doc.Root
            ?? throw new InvalidDataException("Segment XML has no root: " + path);
        if (root.Name.LocalName != "ArrayOfSegmentConfig")
            throw new InvalidDataException(
                "Expected <ArrayOfSegmentConfig>, got <"
                + root.Name.LocalName + ">: " + path);

        var list = new List<UpSegmentDefinition>();
        foreach (var el in root.Elements("SegmentConfig"))
            list.Add(ParseSegment(el, path));
        return list;
    }

    /// <summary>
    /// Convenience: load a platform descriptor and resolve its referenced
    /// segment file from the same directory. Returns a combined
    /// definition. Throws if either file is missing.
    /// </summary>
    public static UpPlatformDefinition LoadPlatformWithSegments(string platformPath)
    {
        var platform = LoadPlatform(platformPath);
        var dir = Path.GetDirectoryName(platformPath) ?? ".";
        var segPath = Path.Combine(dir, platform.SegmentFile);
        if (!File.Exists(segPath))
            throw new FileNotFoundException(
                "SegmentFile '" + platform.SegmentFile
                + "' referenced from " + Path.GetFileName(platformPath)
                + " was not found in " + dir, segPath);
        var segments = LoadSegments(segPath);
        return new UpPlatformDefinition(platform, segments);
    }

    static UpSegmentDefinition ParseSegment(XElement el, string path)
    {
        var name = OptionalText(el, "Name") ?? "";
        var addresses = UpAddressParser.ParsePointerList(OptionalText(el, "Addresses"));
        var pn = ParseOptionalFieldAddress(el, "PNAddr");
        var ver = ParseOptionalFieldAddress(el, "VerAddr");
        var nr = ParseOptionalFieldAddress(el, "SegNrAddr");
        var extra = UpAddressParser.ParseExtraInfo(OptionalText(el, "ExtraInfo"));
        var checkWords = UpAddressParser.ParseCheckWords(OptionalText(el, "CheckWords"));
        var search = ParseSearchAddresses(OptionalText(el, "SearchAddresses"));
        var searchSpec = ParseSearchFor(OptionalText(el, "Searchfor"));
        return new UpSegmentDefinition(
            Name: name,
            Addresses: addresses,
            PartNumberAddress: pn,
            VersionAddress: ver,
            SegmentNumberAddress: nr,
            ExtraInfo: extra,
            CheckWords: checkWords,
            SearchAddresses: search,
            SearchSpec: searchSpec);
    }

    static UpFieldAddress? ParseOptionalFieldAddress(XElement parent, string childName)
    {
        var t = OptionalText(parent, childName);
        if (string.IsNullOrWhiteSpace(t)) return null;
        return UpAddressParser.ParseFieldAddress(t);
    }

    static IReadOnlyList<(int Start, int End)> ParseSearchAddresses(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Array.Empty<(int, int)>();
        var list = new List<(int Start, int End)>();
        foreach (var piece in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = piece.Trim();
            int dash = p.IndexOf('-');
            if (dash < 0)
                throw new UpAddressParseException(
                    "SearchAddresses entry must be 'STARTHEX-ENDHEX'", p);
            var start = int.Parse(
                p.Substring(0, dash).Trim(), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture);
            var end = int.Parse(
                p.Substring(dash + 1).Trim(), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture);
            list.Add((start, end));
        }
        return list;
    }

    static UpSearchSpec? ParseSearchFor(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var parts = raw.Split(':');
        if (parts.Length < 2)
            throw new UpAddressParseException(
                "Searchfor must be MARKER:LENGTH[:FLAG]", raw);
        var markerStr = parts[0].Trim();
        var marker = ulong.Parse(
            markerStr, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var markerSize = (markerStr.Length + 1) / 2;
        var markerOffsetFromBase = int.Parse(
            parts[1].Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var flag = parts.Length >= 3 ? parts[2].Trim() : "";
        return new UpSearchSpec(marker, markerSize, markerOffsetFromBase, flag);
    }

    static string DerivePlatformName(string platformPath)
    {
        var stem = Path.GetFileNameWithoutExtension(platformPath);
        const string suffix = "-platform";
        if (stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            stem = stem.Substring(0, stem.Length - suffix.Length);
        return stem;
    }

    static string? OptionalText(XElement parent, string childName) =>
        parent.Element(childName)?.Value;

    static string RequiredText(XElement parent, string childName, string ctx)
    {
        var v = OptionalText(parent, childName);
        if (string.IsNullOrEmpty(v))
            throw new InvalidDataException(
                "Required element <" + childName + "> missing/empty in " + ctx);
        return v;
    }

    static bool BoolOrDefault(XElement? el, bool defaultValue)
    {
        if (el is null) return defaultValue;
        var t = el.Value.Trim();
        if (bool.TryParse(t, out var b)) return b;
        return defaultValue;
    }
}
