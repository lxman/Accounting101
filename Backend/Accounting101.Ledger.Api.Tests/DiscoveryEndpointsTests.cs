using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>
/// The two discovery endpoints a module uses to populate filter dropdowns without hardcoding a
/// vocabulary: <c>GET /dimensions</c> (the union of <see cref="AccountRequest.RequiredDimensions"/>
/// declared across the client's chart — no journal scan) and <c>GET /source-types</c> (the distinct
/// <see cref="PostEntryRequest.SourceType"/> values actually posted to the journal).
/// </summary>
public sealed class DiscoveryEndpointsTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private static async Task<Guid> CreateAccountAsync(HttpClient http, Guid clientId, AccountRequest request)
    {
        Guid id = Guid.NewGuid();
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{id}", request))
            .EnsureSuccessStatusCode();
        return id;
    }

    private static async Task PostEntryAsync(
        HttpClient http, Guid clientId, Guid debit, Guid credit, decimal amount, string? sourceType)
    {
        PostEntryRequest entry = new(
            null, new DateOnly(2026, 3, 31), null, null,
            [new PostLineRequest(debit, "Debit", amount),
             new PostLineRequest(credit, "Credit", amount)],
            SourceType: sourceType);

        HttpResponseMessage posted = await http.PostAsJsonAsync($"/clients/{clientId}/entries", entry);
        posted.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Dimensions_returns_distinct_required_dimensions_from_the_chart()
    {
        SeededClient c = await fixture.SeedClientAsync();

        // AR requires Customer + Invoice; AP requires Vendor. The union, sorted, is what /dimensions returns.
        await CreateAccountAsync(c.Http, c.ClientId, new AccountRequest
        {
            Number = "1200", Name = "Accounts Receivable", Type = "Asset",
            RequiredDimensions = ["Customer", "Invoice"],
        });
        await CreateAccountAsync(c.Http, c.ClientId, new AccountRequest
        {
            Number = "2000", Name = "Accounts Payable", Type = "Liability",
            RequiredDimensions = ["Vendor"],
        });
        // A plain account with no dimensions should not contribute anything.
        await CreateAccountAsync(c.Http, c.ClientId, new AccountRequest
        {
            Number = "1000", Name = "Cash", Type = "Asset",
        });

        string[]? dims = await c.Http.GetFromJsonAsync<string[]>($"/clients/{c.ClientId}/dimensions");

        Assert.Equal(new[] { "Customer", "Invoice", "Vendor" }, dims!.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Dimensions_is_empty_when_no_account_declares_a_required_dimension()
    {
        SeededClient c = await fixture.SeedClientAsync();

        await CreateAccountAsync(c.Http, c.ClientId, new AccountRequest
        {
            Number = "1000", Name = "Cash", Type = "Asset",
        });

        string[]? dims = await c.Http.GetFromJsonAsync<string[]>($"/clients/{c.ClientId}/dimensions");
        Assert.Empty(dims!);
    }

    [Fact]
    public async Task SourceTypes_returns_distinct_source_types_in_use()
    {
        SeededClient c = await fixture.SeedClientAsync();

        Guid cash = await CreateAccountAsync(c.Http, c.ClientId,
            new AccountRequest { Number = "1000", Name = "Cash", Type = "Asset" });
        Guid revenue = await CreateAccountAsync(c.Http, c.ClientId,
            new AccountRequest { Number = "4000", Name = "Revenue", Type = "Revenue" });
        Guid expense = await CreateAccountAsync(c.Http, c.ClientId,
            new AccountRequest { Number = "5000", Name = "Expense", Type = "Expense" });

        await PostEntryAsync(c.Http, c.ClientId, cash, revenue, 100m, "invoice");
        await PostEntryAsync(c.Http, c.ClientId, cash, revenue, 50m, "invoice");
        await PostEntryAsync(c.Http, c.ClientId, expense, cash, 25m, "bill");
        // An entry with no SourceType must not surface as a phantom value.
        await PostEntryAsync(c.Http, c.ClientId, expense, cash, 10m, null);

        string[]? types = await c.Http.GetFromJsonAsync<string[]>($"/clients/{c.ClientId}/source-types");

        Assert.Equal(new[] { "bill", "invoice" }, types!.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public async Task SourceTypes_is_empty_when_no_entry_carries_a_source_type()
    {
        SeededClient c = await fixture.SeedClientAsync();

        Guid cash = await CreateAccountAsync(c.Http, c.ClientId,
            new AccountRequest { Number = "1000", Name = "Cash", Type = "Asset" });
        Guid revenue = await CreateAccountAsync(c.Http, c.ClientId,
            new AccountRequest { Number = "4000", Name = "Revenue", Type = "Revenue" });

        await PostEntryAsync(c.Http, c.ClientId, cash, revenue, 100m, null);

        string[]? types = await c.Http.GetFromJsonAsync<string[]>($"/clients/{c.ClientId}/source-types");
        Assert.Empty(types!);
    }
}
