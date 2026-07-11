using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>
/// GET /accounts/{id}/balance with an optional 'asOf' query param: absent, it reads the O(1) live
/// projection (unchanged); present, it folds the journal to that date, exactly as the trial balance
/// does, then plucks the single account's balance out of that fold.
/// </summary>
public sealed class AccountBalanceAsOfTests(ApiFixture fixture) : IClassFixture<ApiFixture>
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
    public async Task AccountBalance_with_asOf_folds_to_that_date_and_matches_trial_balance()
    {
        SeededClient c = await fixture.SeedClientAsync(); // Controller: manage accounts, post, approve
        Guid accountA = await PutAccountAsync(c.Http, c.ClientId, "1000", "Cash", "Asset");
        Guid accountB = await PutAccountAsync(c.Http, c.ClientId, "4000", "Revenue", "Revenue");

        await PostAndApproveAsync(c.Http, c.ClientId, new DateOnly(2026, 3, 15), accountA, accountB, 250m);

        // A date BEFORE the entry → zero; a date ON/after → the entry amount.
        AccountBalanceResponse before = (await c.Http.GetFromJsonAsync<AccountBalanceResponse>(
            $"/clients/{c.ClientId}/accounts/{accountA}/balance?asOf=2026-03-14"))!;
        AccountBalanceResponse after = (await c.Http.GetFromJsonAsync<AccountBalanceResponse>(
            $"/clients/{c.ClientId}/accounts/{accountA}/balance?asOf=2026-03-15"))!;
        TrialBalanceResponse tb = (await c.Http.GetFromJsonAsync<TrialBalanceResponse>(
            $"/clients/{c.ClientId}/trial-balance?asOf=2026-03-15"))!;

        Assert.Equal(0m, before.Balance);
        Assert.Equal(tb.Accounts.Single(a => a.AccountId == accountA).Balance, after.Balance);
    }
}
