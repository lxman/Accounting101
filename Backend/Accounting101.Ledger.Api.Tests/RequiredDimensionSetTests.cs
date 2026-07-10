using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

public sealed class RequiredDimensionSetTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private static AccountRequest Asset(string number, string name, IReadOnlyList<string>? dims = null, string? legacy = null) =>
        new() { Number = number, Name = name, Type = "Asset", RequiredDimensions = dims, RequiredDimension = legacy };

    [Fact]
    public async Task Account_with_two_required_dims_rejects_a_line_missing_either()
    {
        SeededClient c = await fixture.SeedClientAsync("ReqDimSetTwo");
        Guid ar = Guid.NewGuid();
        (await c.Http.PutAsJsonAsync($"/clients/{c.ClientId}/accounts/{ar}",
            Asset("1200", "AR", dims: ["Customer", "Invoice"]))).EnsureSuccessStatusCode();
        Guid rev = Guid.NewGuid();
        (await c.Http.PutAsJsonAsync($"/clients/{c.ClientId}/accounts/{rev}",
            new AccountRequest { Number = "4000", Name = "Rev", Type = "Revenue" })).EnsureSuccessStatusCode();

        Guid cust = Guid.NewGuid();
        // Missing "Invoice" → 422 naming it.
        PostEntryRequest missingInvoice = new(null, new DateOnly(2026, 6, 26), "R", "m",
        [
            new PostLineRequest(ar, "Debit", 100m, new Dictionary<string, Guid> { ["Customer"] = cust }),
            new PostLineRequest(rev, "Credit", 100m),
        ]);
        HttpResponseMessage bad = await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/entries", missingInvoice);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        Assert.Contains("Invoice", await bad.Content.ReadAsStringAsync());

        // Both present → OK.
        Guid inv = Guid.NewGuid();
        PostEntryRequest ok = new(null, new DateOnly(2026, 6, 26), "R", "m",
        [
            new PostLineRequest(ar, "Debit", 100m, new Dictionary<string, Guid> { ["Customer"] = cust, ["Invoice"] = inv }),
            new PostLineRequest(rev, "Credit", 100m),
        ]);
        HttpResponseMessage good = await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/entries", ok);
        Assert.Equal(HttpStatusCode.Created, good.StatusCode);
    }

    [Fact]
    public async Task Legacy_single_RequiredDimension_still_enforced_unchanged()
    {
        SeededClient c = await fixture.SeedClientAsync("ReqDimLegacy");
        Guid ar = Guid.NewGuid();
        (await c.Http.PutAsJsonAsync($"/clients/{c.ClientId}/accounts/{ar}",
            Asset("1200", "AR", legacy: "Customer"))).EnsureSuccessStatusCode();
        Guid rev = Guid.NewGuid();
        (await c.Http.PutAsJsonAsync($"/clients/{c.ClientId}/accounts/{rev}",
            new AccountRequest { Number = "4000", Name = "Rev", Type = "Revenue" })).EnsureSuccessStatusCode();

        PostEntryRequest missingCustomer = new(null, new DateOnly(2026, 6, 26), "R", "m",
        [
            new PostLineRequest(ar, "Debit", 100m),
            new PostLineRequest(rev, "Credit", 100m),
        ]);
        HttpResponseMessage bad = await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/entries", missingCustomer);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);

        // Response echoes the dimension in the canonical set.
        AccountResponse acct = (await c.Http.GetFromJsonAsync<AccountResponse>($"/clients/{c.ClientId}/accounts/{ar}"))!;
        Assert.Contains("Customer", acct.RequiredDimensions);
        Assert.Equal("Customer", acct.RequiredDimension);
    }

    [Fact]
    public async Task Subledger_endpoint_accepts_any_dimension_in_the_set()
    {
        SeededClient c = await fixture.SeedClientAsync("ReqDimSubledger");
        Guid ar = Guid.NewGuid();
        (await c.Http.PutAsJsonAsync($"/clients/{c.ClientId}/accounts/{ar}",
            Asset("1200", "AR", dims: ["Customer", "Invoice"]))).EnsureSuccessStatusCode();
        Guid rev = Guid.NewGuid();
        (await c.Http.PutAsJsonAsync($"/clients/{c.ClientId}/accounts/{rev}",
            new AccountRequest { Number = "4000", Name = "Rev", Type = "Revenue" })).EnsureSuccessStatusCode();
        Guid cust = Guid.NewGuid(); Guid inv = Guid.NewGuid();
        PostEntryRequest e = new(null, new DateOnly(2026, 6, 26), "R", "m",
        [
            new PostLineRequest(ar, "Debit", 100m, new Dictionary<string, Guid> { ["Customer"] = cust, ["Invoice"] = inv }),
            new PostLineRequest(rev, "Credit", 100m),
        ]);
        (await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/entries", e)).EnsureSuccessStatusCode();

        // Both axes resolve for the same control account.
        HttpResponseMessage byCustomer = await c.Http.GetAsync($"/clients/{c.ClientId}/subledger?account={ar}&dimension=Customer");
        HttpResponseMessage byInvoice = await c.Http.GetAsync($"/clients/{c.ClientId}/subledger?account={ar}&dimension=Invoice");
        Assert.Equal(HttpStatusCode.OK, byCustomer.StatusCode);
        Assert.Equal(HttpStatusCode.OK, byInvoice.StatusCode);
    }
}
