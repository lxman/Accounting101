using Accounting101.Inventory.Api;
using Accounting101.Ledger.Api.Control;
using Microsoft.Extensions.Configuration;

namespace Accounting101.Inventory.Tests;

file sealed class FakeSource(Dictionary<string, Guid> map) : IPostingAccountsSource
{
    public Task<IReadOnlyDictionary<string, Guid>> GetAsync(Guid clientId, string moduleKey, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, Guid>>(map);
}

public sealed class StoreBackedInventoryAccountsProviderTests
{
    private static readonly string[] Keys =
        ["InventoryAsset", "Cogs", "GrniClearing", "InventoryAdjustment"];

    private static IConfiguration Config(IDictionary<string, string?> entries) =>
        new ConfigurationBuilder().AddInMemoryCollection(entries).Build();

    private static IConfiguration AllConfigured() =>
        Config(Keys.ToDictionary(k => $"Inventory:Accounts:{k}", k => (string?)Guid.NewGuid().ToString()));

    [Fact]
    public async Task Prefers_stored_over_config_for_a_slot()
    {
        Guid stored = Guid.NewGuid();
        var provider = new StoreBackedInventoryAccountsProvider(new FakeSource(new() { ["Cogs"] = stored }), AllConfigured());
        InventoryPostingAccounts got = await provider.GetAccountsAsync(Guid.NewGuid());
        Assert.Equal(stored, got.CogsAccountId);
    }

    [Fact]
    public async Task Falls_back_to_config_when_a_slot_is_unset()
    {
        Guid cogs = Guid.NewGuid();
        Dictionary<string, string?> cfg = Keys.ToDictionary(k => $"Inventory:Accounts:{k}", k => (string?)Guid.NewGuid().ToString());
        cfg["Inventory:Accounts:Cogs"] = cogs.ToString();
        var provider = new StoreBackedInventoryAccountsProvider(new FakeSource(new()), Config(cfg));
        InventoryPostingAccounts got = await provider.GetAccountsAsync(Guid.NewGuid());
        Assert.Equal(cogs, got.CogsAccountId);
    }

    [Fact]
    public async Task Falls_back_to_config_when_a_stored_slot_is_empty_guid()
    {
        Guid cogs = Guid.NewGuid();
        Dictionary<string, string?> cfg = Keys.ToDictionary(k => $"Inventory:Accounts:{k}", k => (string?)Guid.NewGuid().ToString());
        cfg["Inventory:Accounts:Cogs"] = cogs.ToString();
        var provider = new StoreBackedInventoryAccountsProvider(new FakeSource(new() { ["Cogs"] = Guid.Empty }), Config(cfg));
        InventoryPostingAccounts got = await provider.GetAccountsAsync(Guid.NewGuid());
        Assert.Equal(cogs, got.CogsAccountId);
    }

    [Fact]
    public async Task Throws_when_a_slot_has_neither_store_nor_config()
    {
        var provider = new StoreBackedInventoryAccountsProvider(new FakeSource(new()), Config(new Dictionary<string, string?>()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetAccountsAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Maps_all_four_slots_from_the_store()
    {
        Dictionary<string, Guid> map = Keys.ToDictionary(k => k, _ => Guid.NewGuid());
        var provider = new StoreBackedInventoryAccountsProvider(new FakeSource(map), Config(new Dictionary<string, string?>()));
        InventoryPostingAccounts got = await provider.GetAccountsAsync(Guid.NewGuid());
        Assert.Equal(map["InventoryAsset"],      got.InventoryAssetAccountId);
        Assert.Equal(map["Cogs"],                got.CogsAccountId);
        Assert.Equal(map["GrniClearing"],        got.GrniClearingAccountId);
        Assert.Equal(map["InventoryAdjustment"], got.InventoryAdjustmentAccountId);
    }
}
