using Core.Identification.UpXml;
using System.Buffers.Binary;
using Xunit;

namespace EcuSimulator.Tests.Identification.UpXml;

// Unit tests for UpAutoDetector: address-grammar parser, single-rule
// evaluator, group-logic combiner, multi-platform discrimination.
public sealed class UpAutoDetectorTests
{
    [Fact]
    public void ParseAddress_Filesize()
    {
        Assert.IsType<UpDetectAddress.Filesize>(
            UpAutoDetector.ParseAddress("filesize"));
    }

    [Fact]
    public void ParseAddress_LiteralForward()
    {
        var a = UpAutoDetector.ParseAddress("10002:2");
        var lf = Assert.IsType<UpDetectAddress.LiteralForward>(a);
        Assert.Equal(0x10002, lf.Offset);
        Assert.Equal(2, lf.Length);
    }

    [Fact]
    public void ParseAddress_LiteralFromEnd()
    {
        var a = UpAutoDetector.ParseAddress("8@:8");
        var fe = Assert.IsType<UpDetectAddress.LiteralFromEnd>(a);
        Assert.Equal(8, fe.OffsetFromEnd);
        Assert.Equal(8, fe.Length);
    }

    [Fact]
    public void ParseAddress_BareFromEnd_DefaultsLength2()
    {
        var a = UpAutoDetector.ParseAddress("2@");
        var fe = Assert.IsType<UpDetectAddress.LiteralFromEnd>(a);
        Assert.Equal(2, fe.OffsetFromEnd);
        Assert.Equal(2, fe.Length);
    }

    [Fact]
    public void Evaluate_FilesizeEq()
    {
        var rule = new UpDetectRule(
            Xml: "e67.xml", Group: 1, Logic: UpGroupLogic.And,
            Address: new UpDetectAddress.Filesize(),
            Data: 2_097_152, Compare: UpDetectCompare.Eq);
        var bin = new byte[2_097_152];
        Assert.True(UpAutoDetector.Evaluate(new[] { rule }, bin));

        var shorter = new byte[1024];
        Assert.False(UpAutoDetector.Evaluate(new[] { rule }, shorter));
    }

    [Fact]
    public void Evaluate_LiteralFromEnd_SignatureMatch()
    {
        // E38/E67 share this rule: last 8 bytes == 0xAA559966AA5599FF.
        var bin = new byte[0x10000];
        BinaryPrimitives.WriteUInt64BigEndian(
            bin.AsSpan(bin.Length - 8, 8), 0xAA559966AA5599FF);
        var rule = new UpDetectRule(
            Xml: "e38.xml", Group: 1, Logic: UpGroupLogic.And,
            Address: new UpDetectAddress.LiteralFromEnd(8, 8),
            Data: 0xAA559966AA5599FF, Compare: UpDetectCompare.Eq);
        Assert.True(UpAutoDetector.Evaluate(new[] { rule }, bin));

        bin[bin.Length - 1] = 0; // corrupt the last byte
        Assert.False(UpAutoDetector.Evaluate(new[] { rule }, bin));
    }

    [Fact]
    public void GroupLogic_AndRequiresAll_OrRequiresAny()
    {
        var bin = new byte[0x100];
        bin[0x10] = 0x01; bin[0x11] = 0x00;  // at 0x10 BE u16 = 0x0100 = 256
        bin[0x20] = 0x00; bin[0x21] = 0x05;  // at 0x20 BE u16 = 5

        var ruleAt10 = new UpDetectRule(
            "x.xml", 1, UpGroupLogic.And,
            new UpDetectAddress.LiteralForward(0x10, 2), 256, UpDetectCompare.Eq);
        var ruleAt20 = new UpDetectRule(
            "x.xml", 1, UpGroupLogic.And,
            new UpDetectAddress.LiteralForward(0x20, 2), 5, UpDetectCompare.Eq);
        var ruleAt30 = new UpDetectRule(
            "x.xml", 1, UpGroupLogic.And,
            new UpDetectAddress.LiteralForward(0x30, 2), 99, UpDetectCompare.Eq);

        // AND: only matches when ALL rules are true.
        Assert.True(UpAutoDetector.Evaluate(new[] { ruleAt10, ruleAt20 }, bin));
        Assert.False(UpAutoDetector.Evaluate(new[] { ruleAt10, ruleAt30 }, bin));

        // OR: matches when ANY rule is true.
        var orRules = new[]
        {
            ruleAt10 with { Logic = UpGroupLogic.Or },
            ruleAt30 with { Logic = UpGroupLogic.Or },
        };
        Assert.True(UpAutoDetector.Evaluate(orRules, bin));

        var orMisses = new[]
        {
            new UpDetectRule("x.xml", 1, UpGroupLogic.Or,
                new UpDetectAddress.LiteralForward(0x10, 2), 999, UpDetectCompare.Eq),
            new UpDetectRule("x.xml", 1, UpGroupLogic.Or,
                new UpDetectAddress.LiteralForward(0x20, 2), 999, UpDetectCompare.Eq),
        };
        Assert.False(UpAutoDetector.Evaluate(orMisses, bin));
    }

    [Fact]
    public void GroupsAreAnded()
    {
        // Two groups, both must match.
        var bin = new byte[0x100];
        bin[0x10] = 0x01;
        bin[0x20] = 0x05;

        var g1Match = new UpDetectRule(
            "x.xml", 1, UpGroupLogic.And,
            new UpDetectAddress.LiteralForward(0x10, 1), 1, UpDetectCompare.Eq);
        var g2Miss = new UpDetectRule(
            "x.xml", 2, UpGroupLogic.And,
            new UpDetectAddress.LiteralForward(0x20, 1), 99, UpDetectCompare.Eq);
        Assert.False(UpAutoDetector.Evaluate(new[] { g1Match, g2Miss }, bin));

        var g2Match = g2Miss with { Data = 5 };
        Assert.True(UpAutoDetector.Evaluate(new[] { g1Match, g2Match }, bin));
    }

    [Fact]
    public void PickMostSpecific_PrefersHigherRuleCount()
    {
        // E67-shaped ruleset: filesize + last-8-bytes + two byte checks.
        // E38-shaped ruleset: just the last-8-bytes check.
        // A 2MB bin with the right signature satisfies both - E67
        // should win because it has more rules.
        var bin = new byte[2_097_152];
        BinaryPrimitives.WriteUInt64BigEndian(
            bin.AsSpan(bin.Length - 8, 8), 0xAA559966AA5599FF);
        BinaryPrimitives.WriteUInt16BigEndian(bin.AsSpan(0x10002, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(bin.AsSpan(0x3F2, 2), 99);

        var rules = new[]
        {
            // e38 (1 rule)
            new UpDetectRule("e38.xml", 1, UpGroupLogic.And,
                new UpDetectAddress.LiteralFromEnd(8, 8),
                0xAA559966AA5599FF, UpDetectCompare.Eq),
            // e67 (4 rules)
            new UpDetectRule("E67.xml", 1, UpGroupLogic.And,
                new UpDetectAddress.Filesize(),
                2_097_152, UpDetectCompare.Eq),
            new UpDetectRule("E67.xml", 1, UpGroupLogic.And,
                new UpDetectAddress.LiteralFromEnd(8, 8),
                0xAA559966AA5599FF, UpDetectCompare.Eq),
            new UpDetectRule("E67.xml", 1, UpGroupLogic.And,
                new UpDetectAddress.LiteralForward(0x10002, 2),
                1, UpDetectCompare.Eq),
            new UpDetectRule("E67.xml", 1, UpGroupLogic.And,
                new UpDetectAddress.LiteralForward(0x3F2, 2),
                99, UpDetectCompare.Eq),
        };

        // Both match; PickMostSpecific should pick e67.
        var hits = UpAutoDetector.Detect(rules, bin);
        Assert.Contains("e38.xml", hits);
        Assert.Contains("e67.xml", hits);

        var pick = UpAutoDetector.PickMostSpecific(rules, bin);
        Assert.Equal("e67.xml", pick);
    }

    [Fact]
    public void Load_HexdataOverridesDecimalData()
    {
        // Inline parse of one rule with both <data> and <hexdata>. The
        // hexdata value must win - this is the e67.xml-style rule
        // pattern where <data>0</data> is a sentinel and the real
        // value lives in <hexdata>.
        var xml = """
            <?xml version="1.0"?>
            <ArrayOfDetectRule>
              <DetectRule>
                <xml>x.xml</xml>
                <group>1</group>
                <grouplogic>And</grouplogic>
                <address>3f5:4</address>
                <data>0</data>
                <compare>==</compare>
                <hexdata>00C05973</hexdata>
              </DetectRule>
            </ArrayOfDetectRule>
            """;
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, xml);
            var rules = UpAutoDetector.Load(tmp);
            var rule = Assert.Single(rules);
            Assert.Equal(0x00C05973UL, rule.Data);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void LoadFromXml_VendoredAutodetectFile_ProducesE38AndE67Rules()
    {
        var path = LocateVendoredXml("autodetect.xml");
        var rules = UpAutoDetector.Load(path);
        // Sanity: both E38 (1 rule) and E67 (4 rules) target XMLs
        // should appear. Case insensitive because the file mixes
        // "e67.xml" and "E67.xml" - the loader preserves case but the
        // grouping during evaluation must merge them.
        var e38Rules = rules
            .Where(r => string.Equals(r.Xml, "e38.xml", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var e67Rules = rules
            .Where(r => string.Equals(r.Xml, "e67.xml", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.NotEmpty(e38Rules);
        Assert.True(e67Rules.Count >= 4,
            "Expected at least 4 E67 rules, got " + e67Rules.Count);
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
            "Could not locate " + fileName);
    }
}
