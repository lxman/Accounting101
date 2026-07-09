using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Receivables.Tests;

/// <summary>
/// Proves the Task 6 enforcement flip: once A/R requires BOTH the Customer and Invoice dimensions, an
/// untagged (unfoldable) A/R line becomes structurally impossible — the engine rejects it at post, 422,
/// naming the missing dimension. This bypasses every module recipe and posts a hand-built entry directly,
/// so it proves the engine-level guarantee independent of any recipe's own correctness.
/// </summary>
public sealed class ArRequiresInvoiceTests(ReceivablesHostFixture fixture) : IClassFixture<ReceivablesHostFixture>
{
    private async Task SetUpChartAsync(HttpClient http, Guid clientId)
    {
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{fixture.ReceivableAccountId}",
            new AccountRequest
            {
                Number = "1200", Name = "Accounts Receivable", Type = "Asset",
                RequiredDimensions = ["Customer", "Invoice"],
            }))
            .EnsureSuccessStatusCode();
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{fixture.RevenueAccountId}",
            new AccountRequest { Number = "4000", Name = "Revenue", Type = "Revenue" }))
            .EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Raw_AR_line_without_Invoice_dimension_is_rejected()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);

        PostEntryRequest e = new(null, new DateOnly(2026, 6, 26), "R", "m",
        [
            new PostLineRequest(fixture.ReceivableAccountId, "Debit", 100m,
                new Dictionary<string, Guid> { ["Customer"] = Guid.NewGuid() }),   // no Invoice
            new PostLineRequest(fixture.RevenueAccountId, "Credit", 100m),
        ]);
        HttpResponseMessage r = await http.PostAsJsonAsync($"/clients/{clientId}/entries", e);
        Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Contains("Invoice", await r.Content.ReadAsStringAsync());
    }
}
