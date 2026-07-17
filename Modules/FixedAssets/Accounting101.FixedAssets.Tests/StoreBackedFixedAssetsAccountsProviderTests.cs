using Accounting101.FixedAssets.Api;
using Accounting101.Ledger.Api.Control;
using Microsoft.Extensions.Configuration;

namespace Accounting101.FixedAssets.Tests;

file sealed class FakeSource(Dictionary<string, Guid> map) : IPostingAccountsSource
{
    public Task<IReadOnlyDictionary<string, Guid>> GetAsync(Guid clientId, string moduleKey, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, Guid>>(map);
}

public sealed class StoreBackedFixedAssetsAccountsProviderTests
{
    private static readonly string[] Keys =
        ["DepreciationExpense", "AccumulatedDepreciation", "AssetCost", "DisposalProceeds", "GainOnDisposal", "LossOnDisposal"];

    private static IConfiguration Config(IDictionary<string, string?> entries) =>
        new ConfigurationBuilder().AddInMemoryCollection(entries).Build();

    private static IConfiguration AllConfigured() =>
        Config(Keys.ToDictionary(k => $"FixedAssets:Accounts:{k}", k => (string?)Guid.NewGuid().ToString()));

    [Fact]
    public async Task Prefers_stored_over_config_for_a_slot()
    {
        Guid stored = Guid.NewGuid();
        var provider = new StoreBackedFixedAssetsAccountsProvider(new FakeSource(new() { ["AssetCost"] = stored }), AllConfigured());
        FixedAssetsPostingAccounts got = await provider.GetAccountsAsync(Guid.NewGuid());
        Assert.Equal(stored, got.AssetCostAccountId);
    }

    [Fact]
    public async Task Falls_back_to_config_when_a_slot_is_unset()
    {
        Guid assetCost = Guid.NewGuid();
        Dictionary<string, string?> cfg = Keys.ToDictionary(k => $"FixedAssets:Accounts:{k}", k => (string?)Guid.NewGuid().ToString());
        cfg["FixedAssets:Accounts:AssetCost"] = assetCost.ToString();
        var provider = new StoreBackedFixedAssetsAccountsProvider(new FakeSource(new()), Config(cfg));
        FixedAssetsPostingAccounts got = await provider.GetAccountsAsync(Guid.NewGuid());
        Assert.Equal(assetCost, got.AssetCostAccountId);
    }

    [Fact]
    public async Task Falls_back_to_config_when_a_stored_slot_is_empty_guid()
    {
        Guid assetCost = Guid.NewGuid();
        Dictionary<string, string?> cfg = Keys.ToDictionary(k => $"FixedAssets:Accounts:{k}", k => (string?)Guid.NewGuid().ToString());
        cfg["FixedAssets:Accounts:AssetCost"] = assetCost.ToString();
        var provider = new StoreBackedFixedAssetsAccountsProvider(new FakeSource(new() { ["AssetCost"] = Guid.Empty }), Config(cfg));
        FixedAssetsPostingAccounts got = await provider.GetAccountsAsync(Guid.NewGuid());
        Assert.Equal(assetCost, got.AssetCostAccountId);
    }

    [Fact]
    public async Task Throws_when_a_slot_has_neither_store_nor_config()
    {
        var provider = new StoreBackedFixedAssetsAccountsProvider(new FakeSource(new()), Config(new Dictionary<string, string?>()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetAccountsAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Maps_all_six_slots_from_the_store()
    {
        Dictionary<string, Guid> map = Keys.ToDictionary(k => k, _ => Guid.NewGuid());
        var provider = new StoreBackedFixedAssetsAccountsProvider(new FakeSource(map), Config(new Dictionary<string, string?>()));
        FixedAssetsPostingAccounts got = await provider.GetAccountsAsync(Guid.NewGuid());
        Assert.Equal(map["DepreciationExpense"],     got.DepreciationExpenseAccountId);
        Assert.Equal(map["AccumulatedDepreciation"], got.AccumulatedDepreciationAccountId);
        Assert.Equal(map["AssetCost"],               got.AssetCostAccountId);
        Assert.Equal(map["DisposalProceeds"],        got.DisposalProceedsAccountId);
        Assert.Equal(map["GainOnDisposal"],          got.GainOnDisposalAccountId);
        Assert.Equal(map["LossOnDisposal"],          got.LossOnDisposalAccountId);
    }
}
