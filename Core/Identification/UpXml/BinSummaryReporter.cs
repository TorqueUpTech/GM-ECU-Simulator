namespace Core.Identification.UpXml;

// Produces the human-readable summary block shown in the
// UniversalPatcher info tooltip for a bin file:
//
//   BootBlock   PN: 12656811, Ver: AA, Nr: 99 [0000 - BFFF], Size: C000
//   OS          PN: 12656942, Ver: AA, Nr:  1 [10000 - 1BFFFF], Size: 180000
//   ...
//   EEPROM_DATA PN: 12656941, Ver: ABSU         [C000 - DFFF], Size: 2000
//   Eeprom: JH0
//   PCM: 12656941
//   ...
//
// Layout choices to match the screenshot output as closely as we can
// without implementing UP's full CS1/CS2 block decoder:
//
// * Per-segment Start: pointer dereferenced from <Addresses>. This is
//   the segment-header address and matches the screenshot exactly.
// * Per-segment End: the start of the next segment minus 1, except the
//   last segment, whose End is the end of the bin. This will agree with
//   the screenshot for every "contiguous next-segment" case (all
//   non-bootblock segments in the sample E38 bins) and disagree for
//   BootBlock-shaped segments that have a fixed size shorter than the
//   gap to the next segment.
// * Per-segment Size: End - Start + 1 (computed from the bounds above).
//   To get the exact value from the screenshot for BootBlock we'd have
//   to decode the per-segment CS1/CS2 block list inside the segment's
//   own header - deferred to a later stage.
//
// EEPROM_DATA / footer fields come straight from <ExtraInfo>.

using System.Text;

public sealed record BinSummaryLine(
    string Name,
    string PartNumber,
    string Version,
    string SegmentNumber,
    int? StartAddress,
    int? EndAddress,
    int? Size);

public sealed record BinSummary(
    string PlatformName,
    IReadOnlyList<BinSummaryLine> Segments,
    IReadOnlyList<UpResolvedField> Footer,
    IReadOnlyList<string> Diagnostics);

public static class BinSummaryReporter
{
    /// <summary>Build a BinSummary from a loaded platform + bin.</summary>
    public static BinSummary Build(
        UpPlatformDefinition platform, ReadOnlyMemory<byte> bin)
    {
        var resolver = new UpSegmentResolver(bin);
        var resolved = resolver.ResolveAll(platform);

        // First pass: collect segment starts so we can compute End/Size
        // by looking at the next segment's start.
        var starts = new int?[resolved.Count];
        for (int i = 0; i < resolved.Count; i++)
            starts[i] = resolved[i].Base;

        // Determine which (start, position) pairs are in increasing
        // order. Some XMLs list segments out of address order (e.g.
        // EEPROM_DATA comes after Engine but lives at flash 0xC000,
        // below BootBlock's 0). For End/Size we want the next *higher*
        // start across all segments, not the next sibling in the list.
        var sortedStarts = starts
            .Select((s, idx) => (s, idx))
            .Where(t => t.s.HasValue)
            .Select(t => t.s!.Value)
            .Distinct()
            .OrderBy(v => v)
            .ToList();

        var lines = new List<BinSummaryLine>(resolved.Count);
        var diags = new List<string>();
        UpResolvedSegment? eeprom = null;

        for (int i = 0; i < resolved.Count; i++)
        {
            var r = resolved[i];
            foreach (var d in r.Diagnostics)
                diags.Add(r.Name + ": " + d);

            int? end = null;
            int? size = null;
            if (r.Base is int s0)
            {
                // End = (next higher segment start) - 1, or end-of-bin.
                int? nextStart = sortedStarts
                    .Cast<int?>()
                    .FirstOrDefault(v => v > s0);
                if (nextStart is int ns)
                {
                    end = ns - 1;
                    size = ns - s0;
                }
                else
                {
                    end = bin.Length - 1;
                    size = bin.Length - s0;
                }
            }

            lines.Add(new BinSummaryLine(
                Name: r.Name,
                PartNumber: r.PartNumber?.DecodedText ?? "(missing)",
                Version: r.Version?.DecodedText ?? "(missing)",
                SegmentNumber: r.SegmentNumber?.DecodedText ?? "(missing)",
                StartAddress: r.Base,
                EndAddress: end,
                Size: size));

            // Capture the segment whose ExtraInfo carries the footer
            // fields (Eeprom / PCM / PCMid2 / VIN / trace code). In all
            // GM Global-A XMLs we vendor, that's EEPROM_DATA.
            if (eeprom is null && r.ExtraInfo.Count > 0)
                eeprom = r;
        }

        var footer = eeprom?.ExtraInfo ?? Array.Empty<UpResolvedField>();
        return new BinSummary(platform.Platform.Name, lines, footer, diags);
    }

    /// <summary>Render a BinSummary in the same shape as the screenshot.
    /// Field column widths are chosen to align numbers nicely for the
    /// sample E38 bin; longer names just push the columns right.
    /// Addresses are zero-padded to 4 hex digits min to match the
    /// screenshot's "[0000 - BFFF]" style.</summary>
    public static string Format(BinSummary summary)
    {
        var sb = new StringBuilder();
        int nameWidth = Math.Max(11,
            summary.Segments.Count > 0
                ? summary.Segments.Max(s => s.Name.Length)
                : 11);

        foreach (var line in summary.Segments)
        {
            sb.Append(line.Name.PadRight(nameWidth));
            sb.Append(' ');
            sb.Append("PN: ").Append(line.PartNumber).Append(',');
            sb.Append(" Ver: ").Append(line.Version);
            // Some segments (E38 EEPROM_DATA) have no <SegNrAddr> in
            // the XML; the screenshot omits the Nr column in that case
            // rather than rendering "(missing)".
            if (line.SegmentNumber != "(missing)")
                sb.Append(", Nr: ").Append(line.SegmentNumber.PadLeft(2));
            sb.Append(' ');
            if (line.StartAddress is int s && line.EndAddress is int e)
                sb.Append('[').Append(HexAddr(s)).Append(" - ")
                  .Append(HexAddr(e)).Append(']');
            else
                sb.Append("[??? - ???]");
            if (line.Size is int sz)
                sb.Append(", Size: ").Append(sz.ToString("X"));
            sb.AppendLine();
        }

        foreach (var f in summary.Footer)
            sb.Append(f.Label).Append(": ").AppendLine(f.DecodedText);

        return sb.ToString();
    }

    static string HexAddr(int v) => v.ToString("X").PadLeft(4, '0');
}
