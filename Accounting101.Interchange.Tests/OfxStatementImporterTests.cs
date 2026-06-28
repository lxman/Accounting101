using System.Text;

namespace Accounting101.Interchange.Tests;

public sealed class OfxStatementImporterTests
{
    private static ImportResult<ImportedStatement> Import(string ofx, Encoding? enc = null)
    {
        using MemoryStream stream = new((enc ?? Encoding.UTF8).GetBytes(ofx));
        return new OfxStatementImporter().Import(stream, new ImportOptions());
    }

    // Wells-Fargo-shaped: full 1.x header, LEDGERBAL + AVAILBAL, 2 transactions, a DTPOSTED with time+offset.
    private const string WfStyle =
        "OFXHEADER:100\nDATA:OFXSGML\nVERSION:102\nENCODING:USASCII\nCHARSET:1252\n" +
        "<OFX><BANKMSGSRSV1><STMTTRNRS><STMTRS><CURDEF>USD" +
        "<BANKACCTFROM><BANKID>121000248<ACCTID>1234567890<ACCTTYPE>CHECKING</BANKACCTFROM>" +
        "<BANKTRANLIST><DTSTART>20260601<DTEND>20260630" +
        "<STMTTRN><TRNTYPE>CREDIT<DTPOSTED>20260628120000.000[-5:EST]<TRNAMT>1200.00<FITID>A1<NAME>PAYROLL</STMTTRN>" +
        "<STMTTRN><TRNTYPE>DEBIT<DTPOSTED>20260627<TRNAMT>-300.00<FITID>A2<NAME>CHECK 1021<MEMO>RENT</STMTTRN>" +
        "</BANKTRANLIST>" +
        "<LEDGERBAL><BALAMT>900.00<DTASOF>20260630</LEDGERBAL>" +
        "<AVAILBAL><BALAMT>850.00<DTASOF>20260630</AVAILBAL>" +
        "</STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>";

    [Fact]
    public void Parses_a_wells_fargo_style_statement_with_ledger_balance_and_account()
    {
        ImportResult<ImportedStatement> result = Import(WfStyle);
        Assert.Single(result.Records);
        ImportedStatement s = result.Records[0];
        Assert.Equal("1234567890", s.AccountHint);
        Assert.Equal(900.00m, s.ClosingBalance);                       // LEDGERBAL, not AVAILBAL (850)
        Assert.Null(s.OpeningBalance);
        Assert.Equal(new DateOnly(2026, 6, 30), s.StatementDate);
        Assert.Equal(2, s.Lines.Count);
        Assert.Equal(1200.00m, s.Lines[0].Amount);
        Assert.Equal("PAYROLL", s.Lines[0].Description);
        Assert.Equal("A1", s.Lines[0].Reference);                      // FITID
        Assert.Equal(new DateOnly(2026, 6, 28), s.Lines[0].Date);      // time/offset stripped
        Assert.Equal(-300.00m, s.Lines[1].Amount);
        Assert.Equal("CHECK 1021 — RENT", s.Lines[1].Description);     // NAME + MEMO
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Parses_a_headerless_locale_file_with_comma_decimals()
    {
        string fra =
            "<OFX><SIGNONMSGSRSV1><SONRS><STATUS><CODE>0<SEVERITY>INFO</STATUS><DTSERVER>20160414211744<LANGUAGE>FRA</SONRS></SIGNONMSGSRSV1>" +
            "<BANKMSGSRSV1><STMTTRNRS><STMTRS>" +
            "<BANKACCTFROM><BANKID>30002<BRANCHID>00550<ACCTID>FR761234<ACCTTYPE>CHECKING</BANKACCTFROM>" +
            "<BANKTRANLIST><STMTTRN><DTPOSTED>20160410<TRNAMT>-12,34<FITID>F1<NAME>CAFE</STMTTRN></BANKTRANLIST>" +
            "<LEDGERBAL><BALAMT>1000,00<DTASOF>20160414</LEDGERBAL>" +
            "</STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>";
        ImportResult<ImportedStatement> result = Import(fra);
        Assert.Single(result.Records);
        Assert.Equal("FR761234", result.Records[0].AccountHint);
        Assert.Equal(1000.00m, result.Records[0].ClosingBalance);
        Assert.Single(result.Records[0].Lines);
        Assert.Equal(-12.34m, result.Records[0].Lines[0].Amount);       // comma decimal
    }

    [Fact]
    public void A_file_with_multiple_statements_yields_multiple_records()
    {
        string multi =
            "<OFX><BANKMSGSRSV1>" +
            "<STMTTRNRS><STMTRS><BANKACCTFROM><ACCTID>ACCT-A<ACCTTYPE>CHECKING</BANKACCTFROM>" +
            "<BANKTRANLIST><STMTTRN><DTPOSTED>20260601<TRNAMT>10.00<FITID>X1<NAME>A1</STMTTRN></BANKTRANLIST>" +
            "<LEDGERBAL><BALAMT>10.00<DTASOF>20260630</LEDGERBAL></STMTRS></STMTTRNRS>" +
            "<STMTTRNRS><STMTRS><BANKACCTFROM><ACCTID>ACCT-B<ACCTTYPE>SAVINGS</BANKACCTFROM>" +
            "<BANKTRANLIST><STMTTRN><DTPOSTED>20260602<TRNAMT>20.00<FITID>Y1<NAME>B1</STMTTRN></BANKTRANLIST>" +
            "<LEDGERBAL><BALAMT>20.00<DTASOF>20260630</LEDGERBAL></STMTRS></STMTTRNRS>" +
            "</BANKMSGSRSV1></OFX>";
        ImportResult<ImportedStatement> result = Import(multi);
        Assert.Equal(2, result.Records.Count);
        Assert.Equal("ACCT-A", result.Records[0].AccountHint);
        Assert.Equal("ACCT-B", result.Records[1].AccountHint);
    }

    [Fact]
    public void A_transaction_missing_fitid_parses_and_an_empty_amount_warns_and_skips()
    {
        string emptyTags =
            "<OFX><BANKMSGSRSV1><STMTTRNRS><STMTRS><BANKACCTFROM><ACCTID>E1<ACCTTYPE>CHECKING</BANKACCTFROM>" +
            "<BANKTRANLIST>" +
            "<STMTTRN><DTPOSTED>20260601<TRNAMT>5.00<NAME>GOOD-NO-FITID</STMTTRN>" +
            "<STMTTRN><DTPOSTED>20260602<TRNAMT><NAME>EMPTY-AMOUNT</STMTTRN>" +
            "</BANKTRANLIST>" +
            "<LEDGERBAL><BALAMT>5.00<DTASOF>20260630</LEDGERBAL></STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>";
        ImportResult<ImportedStatement> result = Import(emptyTags);
        Assert.Single(result.Records[0].Lines);                         // only the good txn
        Assert.Null(result.Records[0].Lines[0].Reference);              // missing FITID → null
        Assert.Single(result.Warnings);                                 // the empty-amount txn
    }

    [Fact]
    public void An_ofx_2x_xml_input_is_refused()
    {
        string xml =
            "<?xml version=\"1.0\"?><?OFX OFXHEADER=\"200\" VERSION=\"203\" SECURITY=\"NONE\"?>" +
            "<OFX><BANKMSGSRSV1><STMTTRNRS><STMTRS></STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>";
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(xml));
        Assert.Throws<NotSupportedException>(() => new OfxStatementImporter().Import(stream, new ImportOptions()));
    }

    [Fact]
    public void An_error_response_with_no_statement_warns_instead_of_throwing()
    {
        string err =
            "<OFX><SIGNONMSGSRSV1><SONRS><STATUS><CODE>15500<SEVERITY>ERROR<MESSAGE>Invalid login</STATUS></SONRS></SIGNONMSGSRSV1></OFX>";
        ImportResult<ImportedStatement> result = Import(err);
        Assert.Empty(result.Records);
        Assert.Single(result.Warnings);
        Assert.Contains("15500", result.Warnings[0]);
    }
}
