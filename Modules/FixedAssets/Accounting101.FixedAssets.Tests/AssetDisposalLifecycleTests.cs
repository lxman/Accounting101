using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets.Tests;

public sealed class AssetDisposalLifecycleTests(AssetDocumentStoreFixture fixture)
    : IClassFixture<AssetDocumentStoreFixture>
{
    private DocumentAssetStore Store() => new(fixture.Store);

    private static AssetBody Body() =>
        new("Van", 12000m, new DateOnly(2026, 1, 1), 24, 0m, DepreciationMethod.StraightLine, null);

    [Fact]
    public async Task MarkDisposed_sets_disposed_and_final_accumulated_and_returns_prior()
    {
        DocumentAssetStore store = Store();
        Guid clientId = fixture.ClientId;
        Asset a = await store.CreateAsync(clientId, Body(), default);
        // Give it some prior accumulated via a depreciation apply.
        await store.ApplyDepreciationAsync(clientId, [new DepreciationRunLine(a.Id, 2000m)], default);

        DisposeStamp stamp = await store.MarkDisposedAsync(clientId, a.Id, finalAccumulated: 2500m, default);
        Assert.Equal(DisposeOutcome.Disposed, stamp.Outcome);
        Assert.Equal(2000m, stamp.PriorAccumulated);
        Assert.Equal(AssetStatus.Disposed, stamp.Asset!.Status);
        Assert.Equal(2500m, stamp.Asset.AccumulatedDepreciation);

        Asset? reread = await store.GetAsync(clientId, a.Id, default);
        Assert.Equal(AssetStatus.Disposed, reread!.Status);
    }

    [Fact]
    public async Task MarkDisposed_refuses_a_missing_or_already_disposed_asset()
    {
        DocumentAssetStore store = Store();
        Guid clientId = fixture.ClientId;
        Assert.Equal(DisposeOutcome.NotFound, (await store.MarkDisposedAsync(clientId, Guid.NewGuid(), 0m, default)).Outcome);

        Asset a = await store.CreateAsync(clientId, Body(), default);
        await store.MarkDisposedAsync(clientId, a.Id, 0m, default);
        Assert.Equal(DisposeOutcome.NotActive, (await store.MarkDisposedAsync(clientId, a.Id, 0m, default)).Outcome);
    }

    [Fact]
    public async Task Reinstate_restores_active_and_accumulated()
    {
        DocumentAssetStore store = Store();
        Guid clientId = fixture.ClientId;
        Asset a = await store.CreateAsync(clientId, Body(), default);
        await store.MarkDisposedAsync(clientId, a.Id, 2500m, default);

        await store.ReinstateAsync(clientId, a.Id, restoreAccumulated: 2000m, default);
        Asset? reread = await store.GetAsync(clientId, a.Id, default);
        Assert.Equal(AssetStatus.Active, reread!.Status);
        Assert.Equal(2000m, reread.AccumulatedDepreciation);
    }

    [Fact]
    public async Task A_disposed_asset_is_frozen_against_update_deactivate_reactivate()
    {
        DocumentAssetStore store = Store();
        Guid clientId = fixture.ClientId;
        Asset a = await store.CreateAsync(clientId, Body(), default);
        await store.MarkDisposedAsync(clientId, a.Id, 0m, default);

        Assert.Equal(UpdateOutcome.Disposed, (await store.UpdateAsync(clientId, a.Id, Body(), default)).Outcome);
        Assert.Equal(DeactivateResult.Disposed, await store.DeactivateAsync(clientId, a.Id, default));
        Assert.Equal(ReactivateResult.Disposed, await store.ReactivateAsync(clientId, a.Id, default));
    }
}
