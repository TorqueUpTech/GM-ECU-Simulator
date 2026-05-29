namespace Core.Identification.UpXml;

// Loader + evaluator for autodetect.xml. Loader parses the
// <ArrayOfDetectRule> document into a flat List<UpDetectRule>;
// evaluator takes a bin buffer and returns the platform XML names whose
// complete rulesets match.
//
// Tie-breaking when multiple platforms match: caller is given the full
// list, but the helper "PickMostSpecific" returns the one with the
// most rules (most-specific match). This is the lever that picks E67
// (4 rules) over E38 (1 rule) on a Cadillac CTSV bin where both
// rulesets evaluate true.

using System.Buffers.Binary;
using System.Globalization;
using System.Xml.Linq;

public static class UpAutoDetector
{
    /// <summary>
    /// Load autodetect.xml as a flat list of rules. Element names are
    /// matched case-sensitively, attribute values are preserved as written.
    /// </summary>
    public static IReadOnlyList<UpDetectRule> Load(string autodetectXmlPath)
    {
        var doc = XDocument.Load(autodetectXmlPath);
        var root = doc.Root
            ?? throw new InvalidDataException("autodetect XML has no root: " + autodetectXmlPath);
        if (root.Name.LocalName != "ArrayOfDetectRule")
            throw new InvalidDataException(
                "Expected <ArrayOfDetectRule>, got <"
                + root.Name.LocalName + ">: " + autodetectXmlPath);

        var list = new List<UpDetectRule>();
        foreach (var el in root.Elements("DetectRule"))
            list.Add(ParseRule(el));
        return list;
    }

    /// <summary>
    /// Return the lowercased XML stems whose rulesets match the given
    /// bin. autodetect.xml frequently contains two independent rulesets
    /// per target with different cases - "e38.xml" (one rule, the
    /// shared end-of-file signature) and "E38.xml" (a stricter four-
    /// rule check tied to one specific BootBlock PN). UP treats them
    /// as independent evidence paths: matching either lowers-cases to
    /// the same target. We do the same - group case-sensitively, then
    /// fold case variants together at the end.
    /// </summary>
    public static IReadOnlyList<string> Detect(
        IReadOnlyList<UpDetectRule> rules, ReadOnlyMemory<byte> bin)
    {
        var matches = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, ruleset) in GroupRulesCaseSensitive(rules))
        {
            if (Evaluate(ruleset, bin))
                matches.Add(ruleset[0].Xml.ToLowerInvariant());
        }
        var ordered = matches.ToList();
        ordered.Sort(StringComparer.Ordinal);
        return ordered;
    }

    /// <summary>
    /// Pick the single most-specific matching platform. "Most specific"
    /// = the ruleset with the most rules. Across case variants of the
    /// same target, the highest-rule-count variant wins. Ties broken
    /// by alphabetical order for determinism.
    /// </summary>
    public static string? PickMostSpecific(
        IReadOnlyList<UpDetectRule> rules, ReadOnlyMemory<byte> bin)
    {
        (string LowerXml, int RuleCount)? best = null;
        foreach (var (_, ruleset) in GroupRulesCaseSensitive(rules))
        {
            if (!Evaluate(ruleset, bin)) continue;
            var lower = ruleset[0].Xml.ToLowerInvariant();
            if (best is null
                || ruleset.Count > best.Value.RuleCount
                || (ruleset.Count == best.Value.RuleCount
                    && string.CompareOrdinal(lower, best.Value.LowerXml) < 0))
            {
                best = (lower, ruleset.Count);
            }
        }
        return best?.LowerXml;
    }

    static Dictionary<string, List<UpDetectRule>> GroupRulesCaseSensitive(
        IReadOnlyList<UpDetectRule> rules)
    {
        var byXml = new Dictionary<string, List<UpDetectRule>>(StringComparer.Ordinal);
        foreach (var r in rules)
        {
            if (!byXml.TryGetValue(r.Xml, out var bucket))
                byXml[r.Xml] = bucket = new List<UpDetectRule>();
            bucket.Add(r);
        }
        return byXml;
    }

    /// <summary>
    /// Evaluate a single platform's complete ruleset against a bin.
    /// Groups are AND-ed across; within a group the rule's
    /// <see cref="UpDetectRule.Logic"/> applies.
    /// </summary>
    public static bool Evaluate(IReadOnlyList<UpDetectRule> ruleset, ReadOnlyMemory<byte> bin)
    {
        // Group rules by their integer group id and apply group logic.
        var grouped = new Dictionary<int, List<UpDetectRule>>();
        foreach (var r in ruleset)
        {
            if (!grouped.TryGetValue(r.Group, out var bucket))
                grouped[r.Group] = bucket = new List<UpDetectRule>();
            bucket.Add(r);
        }

        foreach (var (_, members) in grouped)
        {
            // All rules in one group share the same Logic value (this
            // is how UP's data is structured). Take the first one as
            // authoritative.
            var logic = members[0].Logic;
            bool groupResult = logic == UpGroupLogic.And;
            foreach (var rule in members)
            {
                var ruleMatched = EvaluateOne(rule, bin);
                if (logic == UpGroupLogic.And)
                {
                    if (!ruleMatched) { groupResult = false; break; }
                }
                else
                {
                    if (ruleMatched) { groupResult = true; break; }
                    groupResult = false;
                }
            }
            if (!groupResult) return false;
        }
        return true;
    }

    static bool EvaluateOne(UpDetectRule rule, ReadOnlyMemory<byte> bin)
    {
        if (!TryReadAddress(rule.Address, bin, out var actual))
            return false;
        return rule.Compare switch
        {
            UpDetectCompare.Eq => actual == rule.Data,
            UpDetectCompare.NotEq => actual != rule.Data,
            UpDetectCompare.Lt => actual < rule.Data,
            UpDetectCompare.Gt => actual > rule.Data,
            _ => false,
        };
    }

    static bool TryReadAddress(UpDetectAddress addr, ReadOnlyMemory<byte> bin, out ulong value)
    {
        value = 0;
        switch (addr)
        {
            case UpDetectAddress.Filesize:
                value = (ulong)bin.Length;
                return true;
            case UpDetectAddress.LiteralForward lf:
                return TryReadBE(bin, lf.Offset, lf.Length, out value);
            case UpDetectAddress.LiteralFromEnd fe:
                {
                    var pos = bin.Length - fe.OffsetFromEnd;
                    return TryReadBE(bin, pos, fe.Length, out value);
                }
            default:
                return false;
        }
    }

    static bool TryReadBE(ReadOnlyMemory<byte> bin, int offset, int len, out ulong v)
    {
        v = 0;
        if (len < 1 || len > 8) return false;
        if (offset < 0 || offset + len > bin.Length) return false;
        var span = bin.Span.Slice(offset, len);
        switch (len)
        {
            case 1: v = span[0]; return true;
            case 2: v = BinaryPrimitives.ReadUInt16BigEndian(span); return true;
            case 4: v = BinaryPrimitives.ReadUInt32BigEndian(span); return true;
            case 8: v = BinaryPrimitives.ReadUInt64BigEndian(span); return true;
            default:
                ulong acc = 0;
                for (int i = 0; i < len; i++) acc = (acc << 8) | span[i];
                v = acc;
                return true;
        }
    }

    static UpDetectRule ParseRule(XElement el)
    {
        string xml = RequiredText(el, "xml");
        int group = int.Parse(
            RequiredText(el, "group"), NumberStyles.Integer, CultureInfo.InvariantCulture);
        var logic = ParseLogic(RequiredText(el, "grouplogic"));
        var address = ParseAddress(RequiredText(el, "address"));
        var compare = ParseCompare(RequiredText(el, "compare"));

        // <hexdata> overrides <data> when present. autodetect.xml uses
        // this pattern to discriminate platform-specific values inside
        // a generic rule shape - e.g. e67.xml has <data>0</data> with
        // <hexdata>00C05973</hexdata> (= 12,605,811 = E67 BootBlock PN
        // variant 1). The <data> field is just a fallback / annotation
        // for legacy rules that predate <hexdata>.
        var hexdataEl = el.Element("hexdata");
        ulong data;
        if (hexdataEl is not null && !string.IsNullOrWhiteSpace(hexdataEl.Value))
        {
            data = ulong.Parse(
                hexdataEl.Value.Trim(),
                NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
        else
        {
            data = ulong.Parse(
                RequiredText(el, "data"),
                NumberStyles.Integer, CultureInfo.InvariantCulture);
        }
        return new UpDetectRule(xml, group, logic, address, data, compare);
    }

    internal static UpDetectAddress ParseAddress(string raw)
    {
        var s = raw.Trim();
        if (s.Equals("filesize", StringComparison.OrdinalIgnoreCase))
            return new UpDetectAddress.Filesize();

        // Forms with explicit length: "HHHH:LEN" or "HHHH@:LEN".
        int colon = s.IndexOf(':');
        if (colon > 0)
        {
            var addrPart = s.Substring(0, colon).Trim();
            var lenStr = s.Substring(colon + 1).Trim();
            var len = int.Parse(
                lenStr, NumberStyles.Integer, CultureInfo.InvariantCulture);
            if (addrPart.EndsWith('@'))
            {
                var off = int.Parse(
                    addrPart.Substring(0, addrPart.Length - 1),
                    NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                return new UpDetectAddress.LiteralFromEnd(off, len);
            }
            var fwd = int.Parse(
                addrPart, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return new UpDetectAddress.LiteralForward(fwd, len);
        }

        // Bare "HHHH@" - length implicit. One instance in the vendored
        // autodetect.xml uses this; treat as LEN=2 (the rule's <data>
        // there is 16-bit width).
        if (s.EndsWith('@'))
        {
            var off = int.Parse(
                s.Substring(0, s.Length - 1),
                NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return new UpDetectAddress.LiteralFromEnd(off, 2);
        }

        throw new UpAddressParseException(
            "Unrecognised autodetect address form", raw);
    }

    static UpGroupLogic ParseLogic(string s) => s.Trim().ToLowerInvariant() switch
    {
        "and" => UpGroupLogic.And,
        "or" => UpGroupLogic.Or,
        _ => throw new InvalidDataException("Unknown grouplogic: '" + s + "'"),
    };

    static UpDetectCompare ParseCompare(string s) => s.Trim() switch
    {
        "==" => UpDetectCompare.Eq,
        "!=" => UpDetectCompare.NotEq,
        "<" => UpDetectCompare.Lt,
        ">" => UpDetectCompare.Gt,
        _ => throw new InvalidDataException("Unknown compare op: '" + s + "'"),
    };

    static string RequiredText(XElement parent, string childName)
    {
        var el = parent.Element(childName)
            ?? throw new InvalidDataException(
                "Missing required <" + childName + "> in <DetectRule>");
        var t = el.Value?.Trim() ?? "";
        if (t.Length == 0)
            throw new InvalidDataException(
                "Empty required <" + childName + "> in <DetectRule>");
        return t;
    }
}
