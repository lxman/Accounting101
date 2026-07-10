using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets.Tests;

// Mirror the fixture wiring of AssetDocumentStoreTests exactly (constructor + attributes).
public sealed class AssetLifecycleStoreTests(AssetDocumentStoreFixture fixture)
    : IClassFixture<AssetDocumentStoreFixture>
{
    private DocumentAssetStore Store() => new(fixture.Store);

    private static AssetBody Body(decimal cost = 12000m, decimal salvage = 0m, int life = 24) =>
        new("Van", cost, new DateOnly(2026, 1, 1), life, salvage, DepreciationMethod.StraightLine, null);

    [Fact]
    public async Task Update_on_a_deactivated_asset_returns_Inactive_and_does_not_resurrect()
    {
        DocumentAssetStore store = Store();
        Guid clientId = fixture.ClientId;
        Asset a = await store.CreateAsync(clientId, Body(), default);
        await store.DeactivateAsync(clientId, a.Id, default);

        UpdateResult result = await store.UpdateAsync(clientId, a.Id, Body(cost: 99999m), default);
        Assert.Equal(UpdateOutcome.Inactive, result.Outcome);

        // Still excluded from the active list (not resurrected).
        PagedResponse<Asset> active = await store.GetByClientPagedAsync(clientId, 0, 50, true, false, default);
        Assert.DoesNotContain(active.Items, x => x.Id == a.Id);
    }

    [Fact]
    public async Task Update_on_missing_asset_returns_NotFound()
    {
        DocumentAssetStore store = Store();
        Guid clientId = fixture.ClientId;
        UpdateResult result = await store.UpdateAsync(clientId, Guid.NewGuid(), Body(), default);
        Assert.Equal(UpdateOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task Update_on_active_asset_updates_and_returns_the_asset()
    {
        DocumentAssetStore store = Store();
        Guid clientId = fixture.ClientId;
        Asset a = await store.CreateAsync(clientId, Body(cost: 12000m), default);
        UpdateResult result = await store.UpdateAsync(clientId, a.Id, Body(cost: 15000m), default);
        Assert.Equal(UpdateOutcome.Updated, result.Outcome);
        Assert.Equal(15000m, result.Asset!.AcquisitionCost);
    }

    [Fact]
    public async Task Reactivate_restores_a_deactivated_asset()
    {
        DocumentAssetStore store = Store();
        Guid clientId = fixture.ClientId;
        Asset a = await store.CreateAsync(clientId, Body(), default);
        await store.DeactivateAsync(clientId, a.Id, default);

        Assert.Equal(ReactivateResult.Reactivated, await store.ReactivateAsync(clientId, a.Id, default));
        // Now editable again.
        UpdateResult upd = await store.UpdateAsync(clientId, a.Id, Body(cost: 13000m), default);
        Assert.Equal(UpdateOutcome.Updated, upd.Outcome);
        // And back in the active list.
        PagedResponse<Asset> active = await store.GetByClientPagedAsync(clientId, 0, 50, true, false, default);
        Assert.Contains(active.Items, x => x.Id == a.Id);
    }

    [Fact]
    public async Task Reactivate_an_active_asset_returns_AlreadyActive()
    {
        DocumentAssetStore store = Store();
        Guid clientId = fixture.ClientId;
        Asset a = await store.CreateAsync(clientId, Body(), default);
        Assert.Equal(ReactivateResult.AlreadyActive, await store.ReactivateAsync(clientId, a.Id, default));
    }

    [Fact]
    public async Task Reactivate_missing_asset_returns_NotFound()
    {
        DocumentAssetStore store = Store();
        Guid clientId = fixture.ClientId;
        Assert.Equal(ReactivateResult.NotFound, await store.ReactivateAsync(clientId, Guid.NewGuid(), default));
    }
}
