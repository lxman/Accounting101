namespace Accounting101.FixedAssets.Tests;

public sealed class AssetDisposalLifecycleTests(AssetDocumentStoreFixture fixture)
    : IClassFixture<AssetDocumentStoreFixture>
{
    private DocumentAssetStore Store() => new(fixture.Store);

    private static AssetBody Body() =>
        new("Van", 12000m, new DateOnly(2026, 1, 1), 24, 0m, DepreciationMethod.StraightLine, null);

    [Fact]
    public async Task MarkDisposed_flips_status_to_disposed()
    {
        DocumentAssetStore store = Store();
        Guid clientId = fixture.ClientId;
        Asset a = await store.CreateAsync(clientId, Body(), default);

        DisposeStamp stamp = await store.MarkDisposedAsync(clientId, a.Id, default);
        Assert.Equal(DisposeOutcome.Disposed, stamp.Outcome);
        Assert.Equal(AssetStatus.Disposed, stamp.Asset!.Status);

        Asset? reread = await store.GetAsync(clientId, a.Id, default);
        Assert.Equal(AssetStatus.Disposed, reread!.Status);
    }

    [Fact]
    public async Task MarkDisposed_refuses_a_missing_or_already_disposed_asset()
    {
        DocumentAssetStore store = Store();
        Guid clientId = fixture.ClientId;
        Assert.Equal(DisposeOutcome.NotFound, (await store.MarkDisposedAsync(clientId, Guid.NewGuid(), default)).Outcome);

        Asset a = await store.CreateAsync(clientId, Body(), default);
        await store.MarkDisposedAsync(clientId, a.Id, default);
        Assert.Equal(DisposeOutcome.NotActive, (await store.MarkDisposedAsync(clientId, a.Id, default)).Outcome);
    }

    [Fact]
    public async Task Reinstate_restores_active_status()
    {
        DocumentAssetStore store = Store();
        Guid clientId = fixture.ClientId;
        Asset a = await store.CreateAsync(clientId, Body(), default);
        await store.MarkDisposedAsync(clientId, a.Id, default);

        await store.ReinstateAsync(clientId, a.Id, default);
        Asset? reread = await store.GetAsync(clientId, a.Id, default);
        Assert.Equal(AssetStatus.Active, reread!.Status);
    }

    [Fact]
    public async Task A_disposed_asset_is_frozen_against_update_deactivate_reactivate()
    {
        DocumentAssetStore store = Store();
        Guid clientId = fixture.ClientId;
        Asset a = await store.CreateAsync(clientId, Body(), default);
        await store.MarkDisposedAsync(clientId, a.Id, default);

        Assert.Equal(UpdateOutcome.Disposed, (await store.UpdateAsync(clientId, a.Id, Body(), default)).Outcome);
        Assert.Equal(DeactivateResult.Disposed, await store.DeactivateAsync(clientId, a.Id, default));
        Assert.Equal(ReactivateResult.Disposed, await store.ReactivateAsync(clientId, a.Id, default));
    }
}
