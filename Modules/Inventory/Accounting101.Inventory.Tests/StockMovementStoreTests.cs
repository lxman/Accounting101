using Accounting101.Ledger.Contracts;

namespace Accounting101.Inventory.Tests;

public sealed class StockMovementStoreTests(StockMovementStoreFixture fixture) : IClassFixture<StockMovementStoreFixture>
{
    private StockMovementStoreFixture Fx => fixture;

    private DocumentStockMovementStore Store() => new(fixture.Store);

    private static StockMovementBody Body(
        Guid itemId, MovementType type, decimal quantity, decimal unitCost, decimal value,
        decimal resultingOnHand, decimal resultingTotalValue) =>
        new(itemId, type, quantity, unitCost, value, resultingOnHand, resultingTotalValue);

    [Fact]
    public async Task Records_number_and_latest_is_per_item()
    {
        DocumentStockMovementStore store = Store();
        Guid itemA = Guid.NewGuid(), itemB = Guid.NewGuid();

        StockMovement a1 = await store.RecordAsync(Fx.ClientId, Body(itemA, MovementType.Receipt, 10m, 2m, 20m, 10m, 20m));
        StockMovement b1 = await store.RecordAsync(Fx.ClientId, Body(itemB, MovementType.Receipt, 5m, 3m, 15m, 5m, 15m));
        StockMovement a2 = await store.RecordAsync(Fx.ClientId, Body(itemA, MovementType.Issue, 4m, 2m, 8m, 6m, 12m));

        Assert.StartsWith("MV-", a1.Number);
        Assert.NotEqual(a1.Number, b1.Number);

        StockMovement? latestA = await store.GetLatestForItemAsync(Fx.ClientId, itemA);
        Assert.Equal(a2.Id, latestA!.Id); // a2, not b1

        StockMovement? latestB = await store.GetLatestForItemAsync(Fx.ClientId, itemB);
        Assert.Equal(b1.Id, latestB!.Id);
    }

    [Fact]
    public async Task Record_stamps_posted_status_and_round_trips_every_field()
    {
        DocumentStockMovementStore store = Store();
        Guid itemId = Guid.NewGuid();

        StockMovement recorded = await store.RecordAsync(
            Fx.ClientId, Body(itemId, MovementType.Receipt, 10m, 2m, 20m, 10m, 20m));

        Assert.Equal(MovementStatus.Posted, recorded.Status);
        Assert.Equal(itemId, recorded.ItemId);
        Assert.Equal(MovementType.Receipt, recorded.Type);
        Assert.Equal(10m, recorded.Quantity);
        Assert.Equal(2m, recorded.UnitCost);
        Assert.Equal(20m, recorded.Value);
        Assert.Equal(10m, recorded.ResultingOnHandQuantity);
        Assert.Equal(20m, recorded.ResultingTotalValue);
        Assert.Equal(20m, recorded.SignedValueEffect);
        Assert.Equal(10m, recorded.SignedQuantityEffect);

        StockMovement? fetched = await store.GetAsync(Fx.ClientId, recorded.Id);
        Assert.NotNull(fetched);
        Assert.Equal(recorded.Number, fetched!.Number);
    }

    [Fact]
    public async Task Issue_signed_effects_are_negative()
    {
        DocumentStockMovementStore store = Store();
        Guid itemId = Guid.NewGuid();

        StockMovement issue = await store.RecordAsync(
            Fx.ClientId, Body(itemId, MovementType.Issue, 4m, 2m, 8m, 6m, 12m));

        Assert.Equal(-4m, issue.SignedQuantityEffect);
        Assert.Equal(-8m, issue.SignedValueEffect);
    }

    [Fact]
    public async Task GetByItemPaged_filters_by_item_and_excludes_voided_unless_requested()
    {
        DocumentStockMovementStore store = Store();
        Guid itemId = Guid.NewGuid();
        Guid otherItemId = Guid.NewGuid();

        StockMovement m1 = await store.RecordAsync(Fx.ClientId, Body(itemId, MovementType.Receipt, 10m, 2m, 20m, 10m, 20m));
        await store.RecordAsync(Fx.ClientId, Body(otherItemId, MovementType.Receipt, 1m, 1m, 1m, 1m, 1m));
        await store.RecordAsync(Fx.ClientId, Body(itemId, MovementType.Issue, 4m, 2m, 8m, 6m, 12m));

        await store.VoidAsync(Fx.ClientId, m1.Id);

        PagedResponse<StockMovement> excl = await store.GetByItemPagedAsync(Fx.ClientId, itemId, 0, 50, true, includeVoided: false);
        Assert.Equal(1, excl.Total);
        Assert.All(excl.Items, m => Assert.Equal(itemId, m.ItemId));

        PagedResponse<StockMovement> incl = await store.GetByItemPagedAsync(Fx.ClientId, itemId, 0, 50, true, includeVoided: true);
        Assert.Equal(2, incl.Total);
        Assert.All(incl.Items, m => Assert.Equal(itemId, m.ItemId));
    }

    [Fact]
    public async Task Void_flips_status_and_excludes_from_latest()
    {
        DocumentStockMovementStore store = Store();
        Guid itemId = Guid.NewGuid();

        StockMovement m1 = await store.RecordAsync(Fx.ClientId, Body(itemId, MovementType.Receipt, 10m, 2m, 20m, 10m, 20m));
        StockMovement m2 = await store.RecordAsync(Fx.ClientId, Body(itemId, MovementType.Issue, 4m, 2m, 8m, 6m, 12m));

        await store.VoidAsync(Fx.ClientId, m2.Id);

        StockMovement? fetched = await store.GetAsync(Fx.ClientId, m2.Id);
        Assert.Equal(MovementStatus.Void, fetched!.Status);

        StockMovement? latest = await store.GetLatestForItemAsync(Fx.ClientId, itemId);
        Assert.Equal(m1.Id, latest!.Id);
    }
}
