using System.Xml.Linq;

namespace Accounting101.Interchange.Tests;

public sealed class OfxNodeTests
{
    // ── SgmlOfxNode delegates to OfxScanner ──────────────────────────────────
    [Fact]
    public void Sgml_leaf_reads_first_unclosed_tag_value()
    {
        var node = new SgmlOfxNode("<A><ACCTID>123<X>y</A>");
        Assert.Equal("123", node.Leaf("ACCTID"));
        Assert.Null(node.Leaf("NOPE"));
    }

    [Fact]
    public void Sgml_blocks_returns_each_aggregate_as_a_node()
    {
        var node = new SgmlOfxNode("<L><STMTTRN><FITID>A</STMTTRN><STMTTRN><FITID>B</STMTTRN></L>");
        IReadOnlyList<IOfxNode> txns = node.Blocks("STMTTRN");
        Assert.Equal(2, txns.Count);
        Assert.Equal("A", txns[0].Leaf("FITID"));
        Assert.Equal("B", txns[1].Leaf("FITID"));
    }

    // ── XmlOfxNode over XElement ─────────────────────────────────────────────
    private static XmlOfxNode Xml(string xml) => new(XDocument.Parse(xml).Root!);

    [Fact]
    public void Xml_leaf_reads_first_descendant_trimmed()
    {
        XmlOfxNode node = Xml("<STMTRS><BANKACCTFROM><ACCTID>  123  </ACCTID></BANKACCTFROM></STMTRS>");
        Assert.Equal("123", node.Leaf("ACCTID"));   // any-depth descendant, trimmed
        Assert.Null(node.Leaf("NOPE"));
    }

    [Fact]
    public void Xml_blocks_are_scoped_to_the_node()
    {
        XmlOfxNode stmt = Xml("<STMTRS><BANKTRANLIST>" +
            "<STMTTRN><FITID>A</FITID></STMTTRN><STMTTRN><FITID>B</FITID></STMTTRN></BANKTRANLIST>" +
            "<LEDGERBAL><BALAMT>900.00</BALAMT></LEDGERBAL></STMTRS>");
        IReadOnlyList<IOfxNode> txns = stmt.Blocks("STMTTRN");
        Assert.Equal(2, txns.Count);
        Assert.Equal("A", txns[0].Leaf("FITID"));
        IReadOnlyList<IOfxNode> ledger = stmt.Blocks("LEDGERBAL");
        Assert.Single(ledger);
        Assert.Equal("900.00", ledger[0].Leaf("BALAMT"));   // scoped to the LEDGERBAL node
    }

    [Fact]
    public void Xml_matches_localname_case_insensitively_and_ignores_namespace()
    {
        XmlOfxNode node = Xml("<OFX xmlns='http://ofx.example'><stmtrs><AcctId>Z9</AcctId></stmtrs></OFX>");
        Assert.Single(node.Blocks("STMTRS"));                       // case-insensitive + namespaced
        Assert.Equal("Z9", node.Blocks("STMTRS")[0].Leaf("ACCTID"));
    }
}
