using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Api.Contracts;
using Accounting101.Ledger.Api.Control;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>
/// Chart-of-accounts management and year-end close through the API: upsert validates the resulting
/// chart, reads return it, only authorized roles may manage it, and a fiscal-year close resets the
/// temporary accounts into retained earnings.
/// </summary>
public sealed class AccountTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private static async Task<Guid> PutAccountAsync(
        HttpClient http, Guid client, string number, string name, string type, bool isRetainedEarnings = false)
    {
        Guid id = Guid.NewGuid();
        AccountRequest request = new() { Number = number, Name = name, Type = type, IsRetainedEarnings = isRetainedEarnings };
        (await http.PutAsJsonAsync($"/clients/{client}/accounts/{id}", request)).EnsureSuccessStatusCode();
        return id;
    }

    private static async Task PostAndApproveAsync(
        HttpClient http, Guid client, long seq, DateOnly date, Guid debit, Guid credit, decimal amount)
    {
        _ = seq; // sequence is engine-assigned now
        PostEntryRequest req = new(null, date, null, null,
            [new PostLineRequest(debit, "Debit", amount), new PostLineRequest(credit, "Credit", amount)]);
        HttpResponseMessage posted = await http.PostAsJsonAsync($"/clients/{client}/entries", req);
        posted.EnsureSuccessStatusCode();
        PostEntryResponse created = (await posted.Content.ReadFromJsonAsync<PostEntryResponse>())!;
        (await http.PostAsync($"/clients/{client}/entries/{created.Id}/approve", null)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Upsert_lists_and_gets_an_account()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid id = Guid.NewGuid();

        HttpResponseMessage put = await c.Http.PutAsJsonAsync(
            $"/clients/{c.ClientId}/accounts/{id}",
            new AccountRequest { Number = "1000", Name = "Cash", Type = "Asset" });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        AccountResponse created = (await put.Content.ReadFromJsonAsync<AccountResponse>())!;
        Assert.Equal("Debit", created.NormalSide); // Asset is debit-normal (derived)

        AccountResponse[] list = (await c.Http.GetFromJsonAsync<AccountResponse[]>($"/clients/{c.ClientId}/accounts"))!;
        Assert.Single(list);

        AccountResponse got = (await c.Http.GetFromJsonAsync<AccountResponse>($"/clients/{c.ClientId}/accounts/{id}"))!;
        Assert.Equal("1000", got.Number);
    }

    [Fact]
    public async Task A_change_that_would_break_the_chart_is_rejected()
    {
        SeededClient c = await fixture.SeedClientAsync();
        await PutAccountAsync(c.Http, c.ClientId, "1000", "Cash", "Asset");

        // A second account reusing the number would make the chart invalid.
        HttpResponseMessage duplicate = await c.Http.PutAsJsonAsync(
            $"/clients/{c.ClientId}/accounts/{Guid.NewGuid()}",
            new AccountRequest { Number = "1000", Name = "Petty Cash", Type = "Asset" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, duplicate.StatusCode);
    }

    [Fact]
    public async Task A_clerk_cannot_manage_accounts()
    {
        SeededClient c = await fixture.SeedClientAsync(role: LedgerRole.Clerk);

        HttpResponseMessage put = await c.Http.PutAsJsonAsync(
            $"/clients/{c.ClientId}/accounts/{Guid.NewGuid()}",
            new AccountRequest { Number = "1000", Name = "Cash", Type = "Asset" });
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
    }

    [Fact]
    public async Task Year_end_close_resets_temporaries_into_retained_earnings()
    {
        SeededClient c = await fixture.SeedClientAsync(); // Controller: manage accounts, post, approve, close
        Guid cash = await PutAccountAsync(c.Http, c.ClientId, "1000", "Cash", "Asset");
        Guid revenue = await PutAccountAsync(c.Http, c.ClientId, "4000", "Revenue", "Revenue");
        Guid expense = await PutAccountAsync(c.Http, c.ClientId, "5000", "Expense", "Expense");
        Guid retained = await PutAccountAsync(c.Http, c.ClientId, "3900", "Retained Earnings", "Equity", isRetainedEarnings: true);

        await PostAndApproveAsync(c.Http, c.ClientId, 1, new DateOnly(2026, 6, 30), cash, revenue, 1000m); // sale
        await PostAndApproveAsync(c.Http, c.ClientId, 2, new DateOnly(2026, 9, 30), expense, cash, 600m);   // cost

        HttpResponseMessage closed = await c.Http.PostAsJsonAsync(
            $"/clients/{c.ClientId}/periods/close-year", new CloseYearRequest(new DateOnly(2026, 12, 31)));
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
        CloseYearResponse result = (await closed.Content.ReadFromJsonAsync<CloseYearResponse>())!;
        Assert.NotNull(result.ClosingEntry);
        Assert.Equal("Closing", result.ClosingEntry!.Type);

        TrialBalanceResponse tb = (await c.Http.GetFromJsonAsync<TrialBalanceResponse>($"/clients/{c.ClientId}/trial-balance"))!;
        decimal Bal(Guid a) => tb.Accounts.SingleOrDefault(x => x.AccountId == a)?.Balance ?? 0m;

        Assert.Equal(0m, Bal(revenue));    // temporary reset
        Assert.Equal(0m, Bal(expense));    // temporary reset
        Assert.Equal(-400m, Bal(retained)); // net income (1000 - 600) rolled into retained earnings
        Assert.Equal(400m, Bal(cash));      // permanent — carried forward
    }
}
