using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets.Tests;

public sealed class DisposalStoreTests(DisposalStoreFixture fixture) : IClassFixture<DisposalStoreFixture>
{
    private DocumentDisposalStore Store() => new(fixture.Store);

    private static DisposalBody Body(Guid assetId, decimal proceeds = 8000m) =>
        new(assetId, new DateOnly(2026, 6, 30), proceeds, CatchUpDepreciation: 500m,
            AccumulatedBeforeDisposal: 5000m, AccumulatedAtDisposal: 5500m,
            NetBookValue: 6500m, GainLoss: proceeds - 6500m, Memo: null);

    [Fact]
    public async Task Record_assigns_a_number_and_posted_status_and_round_trips()
    {
        DocumentDisposalStore store = Store();
        Guid clientId = fixture.NewClient();
        Guid asset = Guid.NewGuid();

        Disposal d = await store.RecordAsync(clientId, Body(asset), default);
        Assert.NotNull(d.Number);
        Assert.Equal(DisposalStatus.Posted, d.Status);
        Assert.Equal(asset, d.AssetId);
        Assert.Equal(1500m, d.GainLoss);

        Disposal? fetched = await store.GetAsync(clientId, d.Id, default);
        Assert.NotNull(fetched);
        Assert.Equal(5000m, fetched!.AccumulatedBeforeDisposal);
    }

    [Fact]
    public async Task GetActiveByAsset_finds_a_non_voided_disposal_and_ignores_voided()
    {
        DocumentDisposalStore store = Store();
        Guid clientId = fixture.NewClient();
        Guid asset = Guid.NewGuid();

        Disposal d = await store.RecordAsync(clientId, Body(asset), default);
        Assert.NotNull(await store.GetActiveByAssetAsync(clientId, asset, default));
        Assert.Null(await store.GetActiveByAssetAsync(clientId, Guid.NewGuid(), default));

        await store.VoidAsync(clientId, d.Id, default);
        Assert.Null(await store.GetActiveByAssetAsync(clientId, asset, default));
    }

    [Fact]
    public async Task Paged_list_excludes_voided_unless_requested()
    {
        DocumentDisposalStore store = Store();
        Guid clientId = fixture.NewClient();

        Disposal a = await store.RecordAsync(clientId, Body(Guid.NewGuid()), default);
        await store.RecordAsync(clientId, Body(Guid.NewGuid()), default);
        await store.VoidAsync(clientId, a.Id, default);

        PagedResponse<Disposal> excl = await store.GetByClientPagedAsync(clientId, 0, 50, true, false, default);
        Assert.Equal(1, excl.Total);
        PagedResponse<Disposal> incl = await store.GetByClientPagedAsync(clientId, 0, 50, true, true, default);
        Assert.Equal(2, incl.Total);
    }
}
