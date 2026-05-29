using Core.Identification.UpXml;
using Xunit;

namespace EcuSimulator.Tests.Identification.UpXml;

// Unit tests for the address mini-language parser. Each test isolates
// one grammar form so a regression points at the broken production.
public sealed class UpAddressParserTests
{
    [Fact]
    public void ParsePointerList_SinglePointer()
    {
        var got = UpAddressParser.ParsePointerList("@10024");
        var p = Assert.IsType<UpPointerTerm.Single>(Assert.Single(got));
        Assert.Equal(0x10024, p.FileOffset);
    }

    [Fact]
    public void ParsePointerList_ArrayPointer()
    {
        var got = UpAddressParser.ParsePointerList("@41c*3");
        var p = Assert.IsType<UpPointerTerm.Array>(Assert.Single(got));
        Assert.Equal(0x41C, p.FileOffset);
        Assert.Equal(3, p.Count);
    }

    [Fact]
    public void ParsePointerList_CommaList()
    {
        var got = UpAddressParser.ParsePointerList("@C0126,@C012E");
        Assert.Equal(2, got.Count);
        Assert.Equal(0xC0126, Assert.IsType<UpPointerTerm.Single>(got[0]).FileOffset);
        Assert.Equal(0xC012E, Assert.IsType<UpPointerTerm.Single>(got[1]).FileOffset);
    }

    [Fact]
    public void ParsePointerList_SearchKeyword()
    {
        var got = UpAddressParser.ParsePointerList("Search");
        Assert.IsType<UpPointerTerm.SearchMarker>(Assert.Single(got));
    }

    [Fact]
    public void ParsePointerList_EmptyReturnsEmpty()
    {
        Assert.Empty(UpAddressParser.ParsePointerList(""));
        Assert.Empty(UpAddressParser.ParsePointerList(null));
        Assert.Empty(UpAddressParser.ParsePointerList("   "));
    }

    [Fact]
    public void ParseFieldAddress_SegmentRelative_Int()
    {
        var got = UpAddressParser.ParseFieldAddress("#5:4:int");
        var sr = Assert.IsType<UpFieldAddress.SegmentRelative>(got);
        Assert.Equal(0x05, sr.Offset);
        Assert.Equal(4, sr.Length);
        Assert.Equal(UpFieldType.Int, sr.Type);
    }

    [Fact]
    public void ParseFieldAddress_SegmentRelative_Text()
    {
        var got = UpAddressParser.ParseFieldAddress("#9:2:text");
        var sr = Assert.IsType<UpFieldAddress.SegmentRelative>(got);
        Assert.Equal(0x09, sr.Offset);
        Assert.Equal(2, sr.Length);
        Assert.Equal(UpFieldType.Text, sr.Type);
    }

    [Fact]
    public void ParseFieldAddress_SegmentRelative_Hex()
    {
        var got = UpAddressParser.ParseFieldAddress("#3F0:2:hex");
        var sr = Assert.IsType<UpFieldAddress.SegmentRelative>(got);
        Assert.Equal(0x3F0, sr.Offset);
        Assert.Equal(UpFieldType.Hex, sr.Type);
    }

    [Fact]
    public void ParseFieldAddress_CheckWordRelative()
    {
        // E38 EEPROM_DATA: "VIN:CWvin+0:17:text" - the field part after
        // the label is "CWvin+0:17:text".
        var got = UpAddressParser.ParseFieldAddress("CWvin+0:17:text");
        var cw = Assert.IsType<UpFieldAddress.CheckWordRelative>(got);
        Assert.Equal("CWvin", cw.Name);
        Assert.Equal(0, cw.Offset);
        Assert.Equal(17, cw.Length);
        Assert.Equal(UpFieldType.Text, cw.Type);
    }

    [Fact]
    public void ParseExtraInfo_FullE38Payload()
    {
        // Verbatim from e38.xml EEPROM_DATA ExtraInfo.
        var got = UpAddressParser.ParseExtraInfo(
            "Eeprom:#7:3:text,PCM:#28:4:int,PCMid2:#20:4:int,VIN:CWvin+0:17:text,trace code:#2c:16:text");
        Assert.Equal(5, got.Count);

        Assert.Equal("Eeprom", got[0].Label);
        Assert.Equal(UpFieldType.Text,
            Assert.IsType<UpFieldAddress.SegmentRelative>(got[0].Address).Type);

        Assert.Equal("VIN", got[3].Label);
        var vinAddr = Assert.IsType<UpFieldAddress.CheckWordRelative>(got[3].Address);
        Assert.Equal("CWvin", vinAddr.Name);
        Assert.Equal(17, vinAddr.Length);

        Assert.Equal("trace code", got[4].Label);
        Assert.Equal(0x2C,
            Assert.IsType<UpFieldAddress.SegmentRelative>(got[4].Address).Offset);
    }

    [Fact]
    public void ParseCheckWords_FullE38Payload()
    {
        // Verbatim from e38.xml EEPROM_DATA CheckWords.
        var got = UpAddressParser.ParseCheckWords(
            "a5a0:326:1cc:CWvin,a5a0:1da:b4:CWvin,a5a0:1be:b8:CWvin");
        Assert.Equal(3, got.Count);
        foreach (var c in got)
        {
            Assert.Equal("CWvin", c.Name);
            Assert.Equal(0xA5A0UL, c.Marker);
            Assert.Equal(2, c.MarkerSize);
        }
        Assert.Equal(0x326, got[0].MarkerOffset);
        Assert.Equal(0x1CC, got[0].AnchorOffset);
        Assert.Equal(0x1DA, got[1].MarkerOffset);
        Assert.Equal(0xB4, got[1].AnchorOffset);
    }

    [Fact]
    public void ParsePointerList_BadInput_Throws()
    {
        Assert.Throws<UpAddressParseException>(
            () => UpAddressParser.ParsePointerList("12345"));
        Assert.Throws<UpAddressParseException>(
            () => UpAddressParser.ParsePointerList("@10*ZZ"));
    }

    [Fact]
    public void ParseFieldAddress_BadInput_Throws()
    {
        Assert.Throws<UpAddressParseException>(
            () => UpAddressParser.ParseFieldAddress("#5"));
        Assert.Throws<UpAddressParseException>(
            () => UpAddressParser.ParseFieldAddress("#5:4:wat"));
        Assert.Throws<UpAddressParseException>(
            () => UpAddressParser.ParseFieldAddress(""));
    }
}
