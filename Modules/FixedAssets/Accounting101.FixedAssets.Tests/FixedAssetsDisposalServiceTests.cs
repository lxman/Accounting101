using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets.Tests;

public sealed class FixedAssetsDisposalServiceTests
{
    private static (FixedAssetsDisposalService svc, InMemoryAssetStore assets, InMemoryDisposalStore disposals, FakeLedgerClient ledger)
        Build()
    {
        InMemoryAssetStore assets = new();
        InMemoryDisposalStore disposals = new();
        FakeLedgerClient ledger = new();
        FixedAccountsProvider accounts = new();
        DepreciationMethodSelector selector = new([new StraightLineDepreciation(), new DecliningBalanceDepreciation()]);
        return (new FixedAssetsDisposalService(assets, disposals, selector, accounts, ledger), assets, disposals, ledger);
    }

    private static AssetBody Sl(decimal cost, int life, DateOnly inService) =>
        new("SL", cost, inService, life, 0m, DepreciationMethod.StraightLine, null);

    [Fact]
    public async Task Dispose_catches_up_depreciation_computes_gain_advances_asset_and_posts_one_entry()
    {
        (FixedAssetsDisposalService svc, InMemoryAssetStore assets, _, FakeLedgerClient ledger) = Build();
        Guid clientId = Guid.NewGuid();
        // 12000 cost, 24mo, 500/mo. In service Jan 2026, dispose Jun 2026 → 5 months → target accum 2500.
        Asset a = await assets.CreateAsync(clientId, Sl(12000m, 24, new DateOnly(2026, 1, 1)), default);

        Disposal d = await svc.DisposeAsync(clientId, a.Id, new DisposeRequest(new DateOnly(2026, 6, 30), 10000m, "sold"), default);

        Assert.Equal(2500m, d.CatchUpDepreciation);
        Assert.Equal(0m, d.AccumulatedBeforeDisposal);
        Assert.Equal(2500m, d.AccumulatedAtDisposal);
        Assert.Equal(9500m, d.NetBookValue);           // 12000 - 2500
        Assert.Equal(500m, d.GainLoss);                // 10000 - 9500 = gain 500
        Asset after = (await assets.GetAsync(clientId, a.Id, default))!;
        Assert.Equal(AssetStatus.Disposed, after.Status);
        Assert.Equal(2500m, after.AccumulatedDepreciation);
        Assert.Single(ledger.Posted);
        Assert.Equal("Disposal", ledger.Posted[0].SourceType);
    }

    [Fact]
    public async Task Disposing_a_non_active_asset_is_rejected()
    {
        (FixedAssetsDisposalService svc, InMemoryAssetStore assets, _, _) = Build();
        Guid clientId = Guid.NewGuid();
        Asset a = await assets.CreateAsync(clientId, Sl(12000m, 24, new DateOnly(2026, 1, 1)), default);
        await svc.DisposeAsync(clientId, a.Id, new DisposeRequest(new DateOnly(2026, 6, 30), 1000m, null), default);
        // Second dispose → already disposed.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.DisposeAsync(clientId, a.Id, new DisposeRequest(new DateOnly(2026, 7, 31), 1000m, null), default));
    }

    [Fact]
    public async Task Disposal_date_before_in_service_is_rejected()
    {
        (FixedAssetsDisposalService svc, InMemoryAssetStore assets, _, _) = Build();
        Guid clientId = Guid.NewGuid();
        Asset a = await assets.CreateAsync(clientId, Sl(12000m, 24, new DateOnly(2026, 6, 1)), default);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.DisposeAsync(clientId, a.Id, new DisposeRequest(new DateOnly(2026, 1, 1), 1000m, null), default));
    }

    [Fact]
    public async Task Void_reverses_the_entry_reinstates_the_asset_and_rolls_accumulated_back()
    {
        (FixedAssetsDisposalService svc, InMemoryAssetStore assets, _, FakeLedgerClient ledger) = Build();
        Guid clientId = Guid.NewGuid();
        Asset a = await assets.CreateAsync(clientId, Sl(12000m, 24, new DateOnly(2026, 1, 1)), default);
        Disposal d = await svc.DisposeAsync(clientId, a.Id, new DisposeRequest(new DateOnly(2026, 6, 30), 10000m, null), default);

        Disposal voided = await svc.VoidDisposalAsync(clientId, d.Id, "unwind", default);
        Assert.Equal(DisposalStatus.Voided, voided.Status);
        Assert.True(ledger.ReversedOrWithdrawn);
        Asset after = (await assets.GetAsync(clientId, a.Id, default))!;
        Assert.Equal(AssetStatus.Active, after.Status);        // reinstated
        Assert.Equal(0m, after.AccumulatedDepreciation);       // rolled back to pre-disposal
    }
}
