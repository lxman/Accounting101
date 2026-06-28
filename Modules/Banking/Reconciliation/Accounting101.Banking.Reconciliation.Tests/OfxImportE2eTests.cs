using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Accounting101.Banking.Reconciliation;
using Accounting101.Banking.Reconciliation.Api;

namespace Accounting101.Banking.Reconciliation.Tests;

/// <summary>End-to-end OFX 1.x import: upload a synthetic OFX/QFX file, get a parse-to-preview with the
/// LEDGERBAL closing balance + account hint, then submit the previewed lines (opening computed so it foots)
/// to the existing statement endpoint. Plus: an OFX 2.x XML upload is refused (422).</summary>
public sealed class OfxImportE2eTests(ReconciliationHostFixture fixture) : IClassFixture<ReconciliationHostFixture>
{
    // Synthetic OFX 1.x SGML (no personal data): 2 txns, LEDGERBAL 900, account 1234567890.
    private const string Ofx1x =
        "OFXHEADER:100\nDATA:OFXSGML\nVERSION:102\nENCODING:USASCII\n" +
        "<OFX><BANKMSGSRSV1><STMTTRNRS><STMTRS><CURDEF>USD" +
        "<BANKACCTFROM><BANKID>121000248<ACCTID>1234567890<ACCTTYPE>CHECKING</BANKACCTFROM>" +
        "<BANKTRANLIST><DTSTART>20260601<DTEND>20260630" +
        "<STMTTRN><TRNTYPE>CREDIT<DTPOSTED>20260628<TRNAMT>1200.00<FITID>A1<NAME>PAYROLL</STMTTRN>" +
        "<STMTTRN><TRNTYPE>DEBIT<DTPOSTED>20260627<TRNAMT>-300.00<FITID>A2<NAME>CHECK 1021</STMTTRN>" +
        "</BANKTRANLIST><LEDGERBAL><BALAMT>900.00<DTASOF>20260630</LEDGERBAL>" +
        "</STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>";

    private const string Ofx2xXml =
        "<?xml version=\"1.0\"?><?OFX OFXHEADER=\"200\" VERSION=\"203\" SECURITY=\"NONE\"?>" +
        "<OFX><BANKMSGSRSV1><STMTTRNRS><STMTRS></STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>";

    private static MultipartFormDataContent Multipart(string ofx, string filename)
    {
        MultipartFormDataContent content = [];
        ByteArrayContent fileContent = new(Encoding.UTF8.GetBytes(ofx));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/x-ofx");
        content.Add(fileContent, "file", filename);
        content.Add(new StringContent("ofx"), "format");
        return content;
    }

    [Fact]
    public async Task Imports_an_ofx_1x_file_to_a_preview_then_submits_a_footing_statement()
    {
        (Guid clientId, _, HttpClient clerk, _) = await fixture.SeedSodClientAsync();

        HttpResponseMessage resp = await clerk.PostAsync(
            $"/clients/{clientId}/bank-statements/import", Multipart(Ofx1x, "Checking.qfx"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        ImportPreviewResponse preview = (await resp.Content.ReadFromJsonAsync<ImportPreviewResponse>())!;

        Assert.Single(preview.Statements);
        StatementPreview s = preview.Statements[0];
        Assert.Equal(2, s.Lines.Count);
        Assert.Equal(900.00m, s.DetectedClosingBalance);
        Assert.Equal("1234567890", s.AccountHint);
        Assert.Empty(preview.Warnings);

        // Submit: opening = closing − Σ lines, so it foots (Σ = 1200 − 300 = 900 → opening 0, closing 900).
        decimal sum = s.Lines.Sum(l => l.Amount);
        decimal closing = s.DetectedClosingBalance!.Value;
        HttpResponseMessage create = await clerk.PostAsJsonAsync($"/clients/{clientId}/bank-statements",
            new RecordBankStatementRequest(fixture.CashAccountId, s.StatementDate ?? new DateOnly(2026, 6, 30),
                closing - sum, closing, s.Lines));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
    }

    [Fact]
    public async Task An_ofx_2x_xml_upload_is_refused_422()
    {
        (Guid clientId, _, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        HttpResponseMessage resp = await clerk.PostAsync(
            $"/clients/{clientId}/bank-statements/import", Multipart(Ofx2xXml, "Checking-v2.ofx"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }
}
