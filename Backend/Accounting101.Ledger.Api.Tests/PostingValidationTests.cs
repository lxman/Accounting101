using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Api.Contracts;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>
/// Posting is validated against the chart of accounts (Finding #1): once a client has a chart, every
/// posted line must hit an account that exists, is active and postable, and carries any dimension its
/// control type requires. A client with no chart yet posts unrestricted (onboarding bootstrap).
/// </summary>
public sealed class PostingValidationTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private static async Task<Guid> CreateAccountAsync(HttpClient http, Guid client, AccountRequest request)
    {
        Guid id = Guid.NewGuid();
        (await http.PutAsJsonAsync($"/clients/{client}/accounts/{id}", request)).EnsureSuccessStatusCode();
        return id;
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient http, Guid client, params PostLineRequest[] lines) =>
        http.PostAsJsonAsync($"/clients/{client}/entries",
            new PostEntryRequest(null, 1, new DateOnly(2026, 3, 31), null, null, lines));

    [Fact]
    public async Task Posting_to_an_account_not_in_the_chart_is_rejected()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid cash = await CreateAccountAsync(c.Http, c.ClientId, new AccountRequest { Number = "1000", Name = "Cash", Type = "Asset" });

        HttpResponseMessage resp = await PostAsync(c.Http, c.ClientId,
            new PostLineRequest(cash, "Debit", 100m),
            new PostLineRequest(Guid.NewGuid(), "Credit", 100m)); // not in the chart
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }

    [Fact]
    public async Task Posting_to_a_summary_account_is_rejected()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid header = await CreateAccountAsync(c.Http, c.ClientId,
            new AccountRequest { Number = "1000", Name = "Assets", Type = "Asset", Postable = false });
        Guid cash = await CreateAccountAsync(c.Http, c.ClientId, new AccountRequest { Number = "1100", Name = "Cash", Type = "Asset" });

        HttpResponseMessage resp = await PostAsync(c.Http, c.ClientId,
            new PostLineRequest(header, "Debit", 100m), // summary — not postable
            new PostLineRequest(cash, "Credit", 100m));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }

    [Fact]
    public async Task Posting_to_an_inactive_account_is_rejected()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid cash = await CreateAccountAsync(c.Http, c.ClientId, new AccountRequest { Number = "1000", Name = "Cash", Type = "Asset" });
        Guid revenue = await CreateAccountAsync(c.Http, c.ClientId, new AccountRequest { Number = "4000", Name = "Revenue", Type = "Revenue" });

        // Deactivate revenue (same id, Active = false).
        (await c.Http.PutAsJsonAsync($"/clients/{c.ClientId}/accounts/{revenue}",
            new AccountRequest { Number = "4000", Name = "Revenue", Type = "Revenue", Active = false })).EnsureSuccessStatusCode();

        HttpResponseMessage resp = await PostAsync(c.Http, c.ClientId,
            new PostLineRequest(cash, "Debit", 100m),
            new PostLineRequest(revenue, "Credit", 100m)); // inactive
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }

    [Fact]
    public async Task A_control_account_requires_its_dimension()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid receivable = await CreateAccountAsync(c.Http, c.ClientId,
            new AccountRequest { Number = "1200", Name = "Accounts Receivable", Type = "Asset", RequiredDimension = "Customer" });
        Guid revenue = await CreateAccountAsync(c.Http, c.ClientId, new AccountRequest { Number = "4000", Name = "Revenue", Type = "Revenue" });

        // Missing the required customer → rejected.
        HttpResponseMessage missing = await PostAsync(c.Http, c.ClientId,
            new PostLineRequest(receivable, "Debit", 100m),
            new PostLineRequest(revenue, "Credit", 100m));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);

        // With the customer → accepted.
        HttpResponseMessage ok = await PostAsync(c.Http, c.ClientId,
            new PostLineRequest(receivable, "Debit", 100m, CustomerId: Guid.NewGuid()),
            new PostLineRequest(revenue, "Credit", 100m));
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
    }

    [Fact]
    public async Task With_no_chart_set_up_posting_is_unrestricted()
    {
        SeededClient c = await fixture.SeedClientAsync(); // no accounts created
        HttpResponseMessage resp = await PostAsync(c.Http, c.ClientId,
            new PostLineRequest(Guid.NewGuid(), "Debit", 100m),
            new PostLineRequest(Guid.NewGuid(), "Credit", 100m));
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }
}
