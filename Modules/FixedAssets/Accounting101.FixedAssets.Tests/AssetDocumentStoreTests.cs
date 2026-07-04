using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets.Tests;

public sealed class AssetDocumentStoreTests(AssetDocumentStoreFixture fixture) : IClassFixture<AssetDocumentStoreFixture>
{
    private DocumentAssetStore Store() => new(fixture.Store);

    private static AssetBody Body(string description = "Van") =>
        new(description, 30000m, new DateOnly(2026, 1, 1), 60, 3000m, DepreciationMethod.StraightLine, null);

    [Fact]
    public async Task Create_stamps_active_status_and_zero_accumulated_depreciation()
    {
        Asset asset = await Store().CreateAsync(fixture.ClientId, Body());
        Assert.NotEqual(Guid.Empty, asset.Id);
        Assert.Equal(AssetStatus.Active, asset.Status);
        Assert.Equal(0m, asset.AccumulatedDepreciation);
        Assert.Equal("Van", asset.Description);
    }

    [Fact]
    public async Task Get_returns_a_created_asset()
    {
        Asset created = await Store().CreateAsync(fixture.ClientId, Body("Forklift"));
        Asset? got = await Store().GetAsync(fixture.ClientId, created.Id);
        Assert.NotNull(got);
        Assert.Equal("Forklift", got!.Description);
    }

    [Fact]
    public async Task Update_changes_editable_params_and_preserves_server_owned_fields()
    {
        Asset created = await Store().CreateAsync(fixture.ClientId, Body("Old"));
        UpdateResult result = await Store().UpdateAsync(fixture.ClientId, created.Id, Body("New") with { UsefulLifeMonths = 36 });
        Assert.Equal(UpdateOutcome.Updated, result.Outcome);
        Asset? updated = result.Asset;
        Assert.NotNull(updated);
        Assert.Equal("New", updated!.Description);
        Assert.Equal(36, updated.UsefulLifeMonths);
        Assert.Equal(AssetStatus.Active, updated.Status);
        Assert.Equal(0m, updated.AccumulatedDepreciation);
    }

    [Fact]
    public async Task Update_of_a_missing_asset_returns_null() =>
        Assert.Equal(UpdateOutcome.NotFound, (await Store().UpdateAsync(fixture.ClientId, Guid.NewGuid(), Body())).Outcome);

    [Fact]
    public async Task Deactivate_removes_the_asset_from_the_default_list_but_include_inactive_shows_it()
    {
        Asset created = await Store().CreateAsync(fixture.ClientId, Body("Retire me"));

        DeactivateResult result = await Store().DeactivateAsync(fixture.ClientId, created.Id);
        Assert.Equal(DeactivateResult.Deactivated, result);

        PagedResponse<Asset> active = await Store().GetByClientPagedAsync(fixture.ClientId, 0, 200, true, includeInactive: false, default);
        Assert.DoesNotContain(active.Items, a => a.Id == created.Id);

        PagedResponse<Asset> all = await Store().GetByClientPagedAsync(fixture.ClientId, 0, 200, true, includeInactive: true, default);
        Assert.Contains(all.Items, a => a.Id == created.Id);
    }

    [Fact]
    public async Task Deactivate_is_not_found_then_conflict_on_repeat()
    {
        Assert.Equal(DeactivateResult.NotFound, await Store().DeactivateAsync(fixture.ClientId, Guid.NewGuid()));

        Asset created = await Store().CreateAsync(fixture.ClientId, Body("Once"));
        Assert.Equal(DeactivateResult.Deactivated, await Store().DeactivateAsync(fixture.ClientId, created.Id));
        Assert.Equal(DeactivateResult.AlreadyInactive, await Store().DeactivateAsync(fixture.ClientId, created.Id));
    }
}
