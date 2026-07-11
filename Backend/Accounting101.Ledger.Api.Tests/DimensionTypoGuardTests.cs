using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>
/// Typo guard: a control account (one with <c>RequiredDimensions</c>) must reject a posting line
/// that carries a dimension key NOT in its declared set. Without this, a misspelled key (e.g.
/// "Custommer" instead of "Customer") is stored silently and the subledger fold — which keys on
/// the declared dimension — never sees it. Non-control accounts (no required dimensions) are
/// untouched: any informational dimension key is still accepted.
/// </summary>
public sealed class DimensionTypoGuardTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private static async Task<Guid> CreateAccountAsync(HttpClient http, Guid clientId, AccountRequest request)
    {
        Guid id = Guid.NewGuid();
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{id}", request)).EnsureSuccessStatusCode();
        return id;
    }

    [Fact]
    public async Task Posting_a_typod_dimension_key_on_a_control_account_returns_422()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid receivable = await CreateAccountAsync(c.Http, c.ClientId,
            new AccountRequest { Number = "1200", Name = "Accounts Receivable", Type = "Asset", RequiredDimensions = ["Customer"] });
        Guid cash = await CreateAccountAsync(c.Http, c.ClientId,
            new AccountRequest { Number = "1000", Name = "Cash", Type = "Asset" });

        Guid cust = Guid.NewGuid();
        HttpResponseMessage res = await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/entries",
            new PostEntryRequest(null, new DateOnly(2026, 3, 31), null, null,
            [
                new PostLineRequest(receivable, "Debit", 100m, new Dictionary<string, Guid> { ["Custommer"] = cust }), // typo
                new PostLineRequest(cash, "Credit", 100m),
            ]));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        ValidationProblemDetails? p = await res.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.NotNull(p);
        Assert.Contains(p!.Errors, e => e.Key.StartsWith("lines[") && e.Value.Any(m => m.Contains("Custommer") && m.Contains("Customer")));
    }

    [Fact]
    public async Task Posting_a_dimension_key_on_a_non_control_account_is_allowed()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid expense = await CreateAccountAsync(c.Http, c.ClientId,
            new AccountRequest { Number = "6000", Name = "Office Supplies", Type = "Expense" }); // no RequiredDimensions
        Guid cash = await CreateAccountAsync(c.Http, c.ClientId,
            new AccountRequest { Number = "1000", Name = "Cash", Type = "Asset" });

        Guid tag = Guid.NewGuid();
        HttpResponseMessage res = await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/entries",
            new PostEntryRequest(null, new DateOnly(2026, 3, 31), null, null,
            [
                new PostLineRequest(expense, "Debit", 50m, new Dictionary<string, Guid> { ["Project"] = tag }), // informational, undeclared
                new PostLineRequest(cash, "Credit", 50m),
            ]));

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
    }
}
