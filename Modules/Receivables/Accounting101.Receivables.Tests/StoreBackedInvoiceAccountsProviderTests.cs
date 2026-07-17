using Accounting101.Ledger.Api.Control;
using Accounting101.Receivables.Api;
using Microsoft.Extensions.Configuration;

namespace Accounting101.Receivables.Tests;

file sealed class FakeSource(Dictionary<string, Guid> map) : IPostingAccountsSource
{
    public Task<IReadOnlyDictionary<string, Guid>> GetAsync(Guid clientId, string moduleKey, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, Guid>>(map);
}

public sealed class StoreBackedInvoiceAccountsProviderTests
{
    private static readonly string[] Keys = ["Receivable", "Revenue", "SalesTaxPayable"];

    private static IConfiguration Config(IDictionary<string, string?> entries) =>
        new ConfigurationBuilder().AddInMemoryCollection(entries).Build();

    private static IConfiguration AllConfigured() =>
        Config(Keys.ToDictionary(k => $"Receivables:Accounts:{k}", k => (string?)Guid.NewGuid().ToString()));

    [Fact]
    public async Task Prefers_stored_over_config_for_a_slot()
    {
        Guid stored = Guid.NewGuid();
        var provider = new StoreBackedInvoiceAccountsProvider(new FakeSource(new() { ["Revenue"] = stored }), AllConfigured());
        InvoicePostingAccounts got = await provider.GetAsync(Guid.NewGuid());
        Assert.Equal(stored, got.DefaultRevenueAccountId);
    }

    [Fact]
    public async Task Falls_back_to_config_when_a_slot_is_unset()
    {
        Guid rev = Guid.NewGuid();
        Dictionary<string, string?> cfg = Keys.ToDictionary(k => $"Receivables:Accounts:{k}", k => (string?)Guid.NewGuid().ToString());
        cfg["Receivables:Accounts:Revenue"] = rev.ToString();
        var provider = new StoreBackedInvoiceAccountsProvider(new FakeSource(new()), Config(cfg));
        InvoicePostingAccounts got = await provider.GetAsync(Guid.NewGuid());
        Assert.Equal(rev, got.DefaultRevenueAccountId);
    }

    [Fact]
    public async Task Falls_back_to_config_when_a_stored_slot_is_empty_guid()
    {
        Guid rev = Guid.NewGuid();
        Dictionary<string, string?> cfg = Keys.ToDictionary(k => $"Receivables:Accounts:{k}", k => (string?)Guid.NewGuid().ToString());
        cfg["Receivables:Accounts:Revenue"] = rev.ToString();
        var provider = new StoreBackedInvoiceAccountsProvider(new FakeSource(new() { ["Revenue"] = Guid.Empty }), Config(cfg));
        InvoicePostingAccounts got = await provider.GetAsync(Guid.NewGuid());
        Assert.Equal(rev, got.DefaultRevenueAccountId);
    }

    [Fact]
    public async Task Throws_when_a_slot_has_neither_store_nor_config()
    {
        var provider = new StoreBackedInvoiceAccountsProvider(new FakeSource(new()), Config(new Dictionary<string, string?>()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Maps_all_three_fixed_slots_from_the_store()
    {
        Dictionary<string, Guid> map = Keys.ToDictionary(k => k, _ => Guid.NewGuid());
        var provider = new StoreBackedInvoiceAccountsProvider(new FakeSource(map), Config(new Dictionary<string, string?>()));
        InvoicePostingAccounts got = await provider.GetAsync(Guid.NewGuid());
        Assert.Equal(map["Receivable"],      got.ReceivableAccountId);
        Assert.Equal(map["Revenue"],         got.DefaultRevenueAccountId);
        Assert.Equal(map["SalesTaxPayable"], got.SalesTaxPayableAccountId);
    }

    [Fact]
    public async Task Reads_revenue_category_map_from_config_even_with_stored_fixed_slots()
    {
        Guid license = Guid.NewGuid();
        Dictionary<string, string?> cfg = Keys.ToDictionary(k => $"Receivables:Accounts:{k}", k => (string?)Guid.NewGuid().ToString());
        cfg["Receivables:Accounts:RevenueByCategory:License"] = license.ToString();
        Dictionary<string, Guid> map = Keys.ToDictionary(k => k, _ => Guid.NewGuid()); // store overrides fixed slots
        var provider = new StoreBackedInvoiceAccountsProvider(new FakeSource(map), Config(cfg));
        InvoicePostingAccounts got = await provider.GetAsync(Guid.NewGuid());
        Assert.Equal(license, got.RevenueAccountsByCategory["License"]);
        Assert.Single(got.RevenueAccountsByCategory);
    }
}
