using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>
/// Balance/trial-balance/subledger responses carry the account's number and name alongside its id,
/// so callers don't have to round-trip the chart of accounts just to label a balance.
/// </summary>
public sealed class AccountLabelingTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private static async Task<Guid> PutAccountAsync(
        HttpClient http, Guid client, string number, string name, string type)
    {
        Guid id = Guid.NewGuid();
        AccountRequest request = new() { Number = number, Name = name, Type = type };
        (await http.PutAsJsonAsync($"/clients/{client}/accounts/{id}", request)).EnsureSuccessStatusCode();
        return id;
    }

    private static async Task PostAndApproveAsync(
        HttpClient http, Guid client, DateOnly date, Guid debit, Guid credit, decimal amount)
    {
        PostEntryRequest req = new(null, date, null, null,
            [new PostLineRequest(debit, "Debit", amount), new PostLineRequest(credit, "Credit", amount)]);
        HttpResponseMessage posted = await http.PostAsJsonAsync($"/clients/{client}/entries", req);
        posted.EnsureSuccessStatusCode();
        PostEntryResponse created = (await posted.Content.ReadFromJsonAsync<PostEntryResponse>())!;
        (await http.PostAsync($"/clients/{client}/entries/{created.Id}/approve", null)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task AccountBalance_and_trial_balance_carry_account_number_and_name()
    {
        SeededClient c = await fixture.SeedClientAsync(); // Controller: manage accounts, post, approve
        Guid cash = await PutAccountAsync(c.Http, c.ClientId, "1000", "Cash", "Asset");
        Guid revenue = await PutAccountAsync(c.Http, c.ClientId, "4000", "Revenue", "Revenue");

        await PostAndApproveAsync(c.Http, c.ClientId, new DateOnly(2026, 3, 15), cash, revenue, 250m);

        AccountBalanceResponse bal = (await c.Http.GetFromJsonAsync<AccountBalanceResponse>(
            $"/clients/{c.ClientId}/accounts/{cash}/balance"))!;
        Assert.Equal("1000", bal.Number);
        Assert.Equal("Cash", bal.Name);

        TrialBalanceResponse tb = (await c.Http.GetFromJsonAsync<TrialBalanceResponse>(
            $"/clients/{c.ClientId}/trial-balance"))!;
        AccountBalanceResponse line = tb.Accounts.Single(a => a.AccountId == cash);
        Assert.Equal("1000", line.Number);
        Assert.Equal("Cash", line.Name);
    }
}
