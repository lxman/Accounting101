namespace Accounting101.Interchange.Tests;

public sealed class OfxScannerTests
{
    [Fact]
    public void Leaf_reads_an_unclosed_value_up_to_the_next_tag()
    {
        Assert.Equal("INFO", OfxScanner.Leaf("<CODE>0<SEVERITY>INFO</STATUS>", "SEVERITY"));
        Assert.Equal("-12.34", OfxScanner.Leaf("<TRNAMT>-12.34<FITID>X1", "TRNAMT"));
        Assert.Equal("1234", OfxScanner.Leaf("<INTU.BID>1234<INTU.USERID>u", "INTU.BID")); // dotted tag
        Assert.Null(OfxScanner.Leaf("<NAME>x", "MISSING"));
    }

    [Fact]
    public void Blocks_returns_each_closed_aggregate_body()
    {
        string s = "<STMTTRN><FITID>A</STMTTRN><STMTTRN><FITID>B</STMTTRN>";
        var blocks = OfxScanner.Blocks(s, "STMTTRN");
        Assert.Equal(2, blocks.Count);
        Assert.Equal("A", OfxScanner.Leaf(blocks[0], "FITID"));
        Assert.Equal("B", OfxScanner.Leaf(blocks[1], "FITID"));
    }

    [Fact]
    public void TryParseOfxDate_takes_the_leading_eight_digits()
    {
        Assert.True(OfxScanner.TryParseOfxDate("20260628", out DateOnly d1));
        Assert.Equal(new DateOnly(2026, 6, 28), d1);
        Assert.True(OfxScanner.TryParseOfxDate("20260628120000.000[-5:EST]", out DateOnly d2));
        Assert.Equal(new DateOnly(2026, 6, 28), d2);
        Assert.False(OfxScanner.TryParseOfxDate("2026", out _));
        Assert.False(OfxScanner.TryParseOfxDate("", out _));
    }

    [Fact]
    public void TryParseOfxAmount_handles_dot_and_comma_decimals()
    {
        Assert.True(OfxScanner.TryParseOfxAmount("-12.34", out decimal a1)); Assert.Equal(-12.34m, a1);
        Assert.True(OfxScanner.TryParseOfxAmount("1234.56", out decimal a2)); Assert.Equal(1234.56m, a2);
        Assert.True(OfxScanner.TryParseOfxAmount("-12,34", out decimal a3)); Assert.Equal(-12.34m, a3); // locale comma
        Assert.False(OfxScanner.TryParseOfxAmount("", out _));
        Assert.False(OfxScanner.TryParseOfxAmount("abc", out _));
    }

    [Fact]
    public void StripHeaderAndDetectDialect_strips_1x_header_and_flags_2x()
    {
        string wf = "OFXHEADER:100\nDATA:OFXSGML\nVERSION:102\n<OFX><BANK></OFX>";
        string body = OfxScanner.StripHeaderAndDetectDialect(wf, out bool wfXml);
        Assert.StartsWith("<OFX", body);
        Assert.False(wfXml);

        OfxScanner.StripHeaderAndDetectDialect("<OFX><BANK></OFX>", out bool headerlessXml); // no preamble
        Assert.False(headerlessXml);

        OfxScanner.StripHeaderAndDetectDialect("<?xml version=\"1.0\"?><?OFX OFXHEADER=\"200\" VERSION=\"203\"?><OFX/>", out bool xml);
        Assert.True(xml);
    }
}
