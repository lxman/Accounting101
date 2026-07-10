using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Payables.Tests;

/// <summary>
/// Proves the Task 5 enforcement flip: once A/P requires BOTH the Vendor and Bill dimensions, an
/// untagged (unfoldable) A/P line becomes structurally impossible — the engine rejects it at post, 422,
/// naming the missing dimension. This bypasses every module recipe and posts a hand-built entry directly,
/// so it proves the engine-level guarantee independent of any recipe's own correctness.
/// </summary>
public sealed class ApRequiresBillTests(PayablesHostFixture fixture) : IClassFixture<PayablesHostFixture>
{
    private async Task SetUpChartAsync(HttpClient http, Guid clientId)
    {
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{fixture.PayableAccountId}",
            new AccountRequest
            {
                Number = "2000", Name = "Accounts Payable", Type = "Liability",
                RequiredDimensions = ["Vendor", "Bill"],
            }))
            .EnsureSuccessStatusCode();
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{fixture.RentExpenseAccountId}",
            new AccountRequest { Number = "5200", Name = "Rent Expense", Type = "Expense" }))
            .EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Raw_AP_line_without_Bill_dimension_is_rejected()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);

        PostEntryRequest e = new(null, new DateOnly(2026, 6, 26), "R", "m",
        [
            new PostLineRequest(fixture.RentExpenseAccountId, "Debit", 100m),
            new PostLineRequest(fixture.PayableAccountId, "Credit", 100m,
                new Dictionary<string, Guid> { ["Vendor"] = Guid.NewGuid() }),   // no Bill
        ]);
        HttpResponseMessage r = await http.PostAsJsonAsync($"/clients/{clientId}/entries", e);
        Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Contains("Bill", await r.Content.ReadAsStringAsync());
    }
}
