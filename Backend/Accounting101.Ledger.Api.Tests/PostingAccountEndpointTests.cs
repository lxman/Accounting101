using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

public sealed class PostingAccountEndpointTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private async Task<(Guid clientId, HttpClient http)> MemberWithCashEnabledAsync(params string[] caps)
    {
        SeededClient c = await fixture.SeedClientAsync("PostAcct");
        await fixture.Control().SetClientModulesAsync(c.ClientId, new[] { "cash" });
        Guid userId = Guid.NewGuid();
        await fixture.Control().SetMembershipAsync(userId, c.ClientId, [], caps);
        return (c.ClientId, fixture.ClientFor(userId, "Member"));
    }

    [Fact]
    public async Task Get_lists_the_cash_slot_with_null_then_the_saved_value()
    {
        (Guid clientId, HttpClient http) = await MemberWithCashEnabledAsync(Capabilities.AdminPostingAccounts, Capabilities.GlRead);

        PostingAccountsResponse before = (await http.GetFromJsonAsync<PostingAccountsResponse>(
            $"/clients/{clientId}/posting-accounts"))!;
        PostingAccountSlotResponse cash = Assert.Single(before.Slots);
        Assert.Equal("cash", cash.ModuleKey);
        Assert.Equal("Cash", cash.SlotKey);
        Assert.Null(cash.CurrentAccountId);

        Guid account = Guid.NewGuid();
        HttpResponseMessage put = await http.PutAsJsonAsync(
            $"/clients/{clientId}/posting-accounts/cash",
            new SetPostingAccountsRequest(new Dictionary<string, Guid> { ["Cash"] = account }));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        PostingAccountsResponse after = (await http.GetFromJsonAsync<PostingAccountsResponse>(
            $"/clients/{clientId}/posting-accounts"))!;
        Assert.Equal(account, Assert.Single(after.Slots).CurrentAccountId);
    }

    [Fact]
    public async Task Get_omits_slots_for_modules_the_client_has_not_enabled()
    {
        // ApiFixture.SeedClientAsync enables ALL registered modules by default when enabledModules is
        // omitted, so an explicit empty list is required here to exercise "no modules enabled → no slots".
        SeededClient c = await fixture.SeedClientAsync("PostAcctNoMods", enabledModules: []);
        Guid userId = Guid.NewGuid();
        await fixture.Control().SetMembershipAsync(userId, c.ClientId, [], new[] { Capabilities.AdminPostingAccounts, Capabilities.GlRead });
        HttpClient http = fixture.ClientFor(userId, "Member");

        PostingAccountsResponse got = (await http.GetFromJsonAsync<PostingAccountsResponse>(
            $"/clients/{c.ClientId}/posting-accounts"))!;
        Assert.Empty(got.Slots);
    }

    [Fact]
    public async Task Put_rejects_an_unknown_module()
    {
        (Guid clientId, HttpClient http) = await MemberWithCashEnabledAsync(Capabilities.AdminPostingAccounts, Capabilities.GlRead);
        HttpResponseMessage put = await http.PutAsJsonAsync(
            $"/clients/{clientId}/posting-accounts/ghost",
            new SetPostingAccountsRequest(new Dictionary<string, Guid> { ["Cash"] = Guid.NewGuid() }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, put.StatusCode);
    }

    [Fact]
    public async Task Put_rejects_an_unknown_slot()
    {
        (Guid clientId, HttpClient http) = await MemberWithCashEnabledAsync(Capabilities.AdminPostingAccounts, Capabilities.GlRead);
        HttpResponseMessage put = await http.PutAsJsonAsync(
            $"/clients/{clientId}/posting-accounts/cash",
            new SetPostingAccountsRequest(new Dictionary<string, Guid> { ["Bogus"] = Guid.NewGuid() }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, put.StatusCode);
    }

    [Fact]
    public async Task Member_without_cap_is_forbidden()
    {
        (Guid clientId, HttpClient http) = await MemberWithCashEnabledAsync(Capabilities.GlRead);
        HttpResponseMessage resp = await http.GetAsync($"/clients/{clientId}/posting-accounts");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Get_lists_the_five_payroll_slots_and_PUT_validates_them()
    {
        SeededClient c = await fixture.SeedClientAsync("PostAcctPayroll");
        await fixture.Control().SetClientModulesAsync(c.ClientId, new[] { "payroll" });
        Guid userId = Guid.NewGuid();
        await fixture.Control().SetMembershipAsync(userId, c.ClientId, [], new[] { Capabilities.AdminPostingAccounts, Capabilities.GlRead });
        HttpClient http = fixture.ClientFor(userId, "Member");

        PostingAccountsResponse got = (await http.GetFromJsonAsync<PostingAccountsResponse>(
            $"/clients/{c.ClientId}/posting-accounts"))!;
        Assert.Equal(5, got.Slots.Count(s => s.ModuleKey == "payroll"));

        HttpResponseMessage ok = await http.PutAsJsonAsync(
            $"/clients/{c.ClientId}/posting-accounts/payroll",
            new SetPostingAccountsRequest(new Dictionary<string, Guid> { ["SalariesExpense"] = Guid.NewGuid() }));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        HttpResponseMessage bad = await http.PutAsJsonAsync(
            $"/clients/{c.ClientId}/posting-accounts/payroll",
            new SetPostingAccountsRequest(new Dictionary<string, Guid> { ["Nope"] = Guid.NewGuid() }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
    }

    [Fact]
    public async Task Get_lists_the_three_payables_slots_and_PUT_validates_them()
    {
        SeededClient c = await fixture.SeedClientAsync("PostAcctPayables");
        await fixture.Control().SetClientModulesAsync(c.ClientId, new[] { "payables" });
        Guid userId = Guid.NewGuid();
        await fixture.Control().SetMembershipAsync(userId, c.ClientId, [], new[] { Capabilities.AdminPostingAccounts, Capabilities.GlRead });
        HttpClient http = fixture.ClientFor(userId, "Member");

        PostingAccountsResponse got = (await http.GetFromJsonAsync<PostingAccountsResponse>(
            $"/clients/{c.ClientId}/posting-accounts"))!;
        Assert.Equal(3, got.Slots.Count(s => s.ModuleKey == "payables"));

        HttpResponseMessage ok = await http.PutAsJsonAsync(
            $"/clients/{c.ClientId}/posting-accounts/payables",
            new SetPostingAccountsRequest(new Dictionary<string, Guid> { ["VendorCredits"] = Guid.NewGuid() }));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        HttpResponseMessage bad = await http.PutAsJsonAsync(
            $"/clients/{c.ClientId}/posting-accounts/payables",
            new SetPostingAccountsRequest(new Dictionary<string, Guid> { ["Nope"] = Guid.NewGuid() }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
    }

    [Fact]
    public async Task Get_lists_the_six_fixed_assets_slots_and_PUT_validates_them()
    {
        SeededClient c = await fixture.SeedClientAsync("PostAcctFixedAssets");
        await fixture.Control().SetClientModulesAsync(c.ClientId, new[] { "fixedassets" });
        Guid userId = Guid.NewGuid();
        await fixture.Control().SetMembershipAsync(userId, c.ClientId, [], new[] { Capabilities.AdminPostingAccounts, Capabilities.GlRead });
        HttpClient http = fixture.ClientFor(userId, "Member");

        PostingAccountsResponse got = (await http.GetFromJsonAsync<PostingAccountsResponse>(
            $"/clients/{c.ClientId}/posting-accounts"))!;
        Assert.Equal(6, got.Slots.Count(s => s.ModuleKey == "fixedassets"));

        HttpResponseMessage ok = await http.PutAsJsonAsync(
            $"/clients/{c.ClientId}/posting-accounts/fixedassets",
            new SetPostingAccountsRequest(new Dictionary<string, Guid> { ["AccumulatedDepreciation"] = Guid.NewGuid() }));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        HttpResponseMessage bad = await http.PutAsJsonAsync(
            $"/clients/{c.ClientId}/posting-accounts/fixedassets",
            new SetPostingAccountsRequest(new Dictionary<string, Guid> { ["Nope"] = Guid.NewGuid() }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
    }

    [Fact]
    public async Task Get_lists_the_four_inventory_slots_and_PUT_validates_them()
    {
        SeededClient c = await fixture.SeedClientAsync("PostAcctInventory");
        await fixture.Control().SetClientModulesAsync(c.ClientId, new[] { "inventory" });
        Guid userId = Guid.NewGuid();
        await fixture.Control().SetMembershipAsync(userId, c.ClientId, [], new[] { Capabilities.AdminPostingAccounts, Capabilities.GlRead });
        HttpClient http = fixture.ClientFor(userId, "Member");

        PostingAccountsResponse got = (await http.GetFromJsonAsync<PostingAccountsResponse>(
            $"/clients/{c.ClientId}/posting-accounts"))!;
        Assert.Equal(4, got.Slots.Count(s => s.ModuleKey == "inventory"));

        HttpResponseMessage ok = await http.PutAsJsonAsync(
            $"/clients/{c.ClientId}/posting-accounts/inventory",
            new SetPostingAccountsRequest(new Dictionary<string, Guid> { ["InventoryAsset"] = Guid.NewGuid() }));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        HttpResponseMessage bad = await http.PutAsJsonAsync(
            $"/clients/{c.ClientId}/posting-accounts/inventory",
            new SetPostingAccountsRequest(new Dictionary<string, Guid> { ["Nope"] = Guid.NewGuid() }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
    }
}
