using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Accounting101.Banking.Reconciliation;
using Accounting101.Banking.Reconciliation.Api;

namespace Accounting101.Banking.Reconciliation.Tests;

/// <summary>End-to-end import: upload a Wells-Fargo-shaped CSV (synthetic), get a parse-to-preview that
/// excludes Pending rows and warns on a bad row, then submit the previewed lines to the existing statement
/// endpoint — proving the round-trip into a real, footing statement.</summary>
public sealed class StatementImportE2eTests(ReconciliationHostFixture fixture) : IClassFixture<ReconciliationHostFixture>
{
    private const string WfMapping =
        """
        {"date":{"header":"DATE"},"amount":{"header":"AMOUNT"},"description":{"header":"DESCRIPTION"},
         "reference":{"header":"CHECK #"},"status":{"header":"STATUS"},"excludeStatuses":["Pending"],
         "dateFormat":"MM/dd/yyyy","hasHeader":true}
        """;

    private static MultipartFormDataContent Multipart(string csv, string format, string? mapping)
    {
        MultipartFormDataContent content = [];
        ByteArrayContent fileContent = new(Encoding.UTF8.GetBytes(csv));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        content.Add(fileContent, "file", "Checking.csv");
        content.Add(new StringContent(format), "format");
        if (mapping is not null) content.Add(new StringContent(mapping), "mapping");
        return content;
    }

    [Fact]
    public async Task Imports_a_csv_to_a_preview_then_submits_a_footing_statement()
    {
        (Guid clientId, _, HttpClient clerk, _) = await fixture.SeedSodClientAsync();

        string csv =
            "\"DATE\",\"DESCRIPTION\",\"AMOUNT\",\"CHECK #\",\"STATUS\"\n" +
            "\"06/29/2026\",\"PURCHASE COFFEE\",\"-4.50\",\"\",\"Pending\"\n" +    // excluded
            "\"06/28/2026\",\"PAYROLL DEPOSIT\",\"1200.00\",\"\",\"Posted\"\n" +
            "\"06/27/2026\",\"CHECK 1021\",\"-300.00\",\"1021\",\"Posted\"\n";

        HttpResponseMessage resp = await clerk.PostAsync(
            $"/clients/{clientId}/bank-statements/import?ignored=1", Multipart(csv, "csv", WfMapping));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        ImportPreviewResponse preview = (await resp.Content.ReadFromJsonAsync<ImportPreviewResponse>())!;

        Assert.Single(preview.Statements);
        IReadOnlyList<BankStatementLineRequest> lines = preview.Statements[0].Lines;
        Assert.Equal(2, lines.Count);                                  // Pending excluded
        Assert.Equal(1200.00m, lines[0].Amount);
        Assert.Equal(-300.00m, lines[1].Amount);
        Assert.Empty(preview.Warnings);

        // Submit the previewed lines as a real statement: opening 0, closing = Σ lines (it foots).
        decimal closing = lines.Sum(l => l.Amount);
        HttpResponseMessage create = await clerk.PostAsJsonAsync($"/clients/{clientId}/bank-statements",
            new RecordBankStatementRequest(fixture.CashAccountId, new DateOnly(2026, 6, 30), 0m, closing, lines));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
    }

    [Fact]
    public async Task A_missing_mapping_is_rejected_422()
    {
        (Guid clientId, _, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        HttpResponseMessage resp = await clerk.PostAsync(
            $"/clients/{clientId}/bank-statements/import", Multipart("DATE,DESCRIPTION,AMOUNT\n", "csv", mapping: null));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }

    [Fact]
    public async Task An_unparseable_row_lands_in_warnings_and_the_rest_parse()
    {
        (Guid clientId, _, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        string csv =
            "\"DATE\",\"DESCRIPTION\",\"AMOUNT\",\"CHECK #\",\"STATUS\"\n" +
            "\"06/28/2026\",\"GOOD\",\"10.00\",\"\",\"Posted\"\n" +
            "\"NOTADATE\",\"BAD\",\"5.00\",\"\",\"Posted\"\n";

        HttpResponseMessage resp = await clerk.PostAsync(
            $"/clients/{clientId}/bank-statements/import", Multipart(csv, "csv", WfMapping));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        ImportPreviewResponse preview = (await resp.Content.ReadFromJsonAsync<ImportPreviewResponse>())!;
        Assert.Single(preview.Statements[0].Lines);                    // only GOOD
        Assert.Single(preview.Warnings);                               // the NOTADATE row
    }
}
