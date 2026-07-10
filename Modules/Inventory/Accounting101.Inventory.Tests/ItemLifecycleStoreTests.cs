using Accounting101.Ledger.Contracts;

namespace Accounting101.Inventory.Tests;

// Mirror the fixture wiring of ItemDocumentStoreTests exactly (constructor + attributes).
public sealed class ItemLifecycleStoreTests(ItemDocumentStoreFixture fixture)
    : IClassFixture<ItemDocumentStoreFixture>
{
    private DocumentItemStore Store() => new(fixture.Store);

    private static ItemBody Body(string sku = "SKU1", string name = "Widget") =>
        new(sku, name, null, "each");

    [Fact]
    public async Task Update_on_a_deactivated_item_returns_Inactive_and_does_not_resurrect()
    {
        DocumentItemStore store = Store();
        Guid clientId = fixture.ClientId;
        Item i = await store.CreateAsync(clientId, Body(sku: "A1"), default);
        await store.DeactivateAsync(clientId, i.Id, default);

        UpdateResult result = await store.UpdateAsync(clientId, i.Id, Body(sku: "A1", name: "Renamed"), default);
        Assert.Equal(UpdateOutcome.Inactive, result.Outcome);

        // Still excluded from the active list (not resurrected).
        PagedResponse<Item> active = await store.GetByClientPagedAsync(clientId, 0, 50, true, false, default);
        Assert.DoesNotContain(active.Items, x => x.Id == i.Id);
    }

    [Fact]
    public async Task Update_on_missing_item_returns_NotFound()
    {
        DocumentItemStore store = Store();
        Guid clientId = fixture.ClientId;
        UpdateResult result = await store.UpdateAsync(clientId, Guid.NewGuid(), Body(sku: "A2"), default);
        Assert.Equal(UpdateOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task Update_on_active_item_updates_and_returns_the_item()
    {
        DocumentItemStore store = Store();
        Guid clientId = fixture.ClientId;
        Item i = await store.CreateAsync(clientId, Body(sku: "A3", name: "Old"), default);
        UpdateResult result = await store.UpdateAsync(clientId, i.Id, Body(sku: "A3", name: "New"), default);
        Assert.Equal(UpdateOutcome.Updated, result.Outcome);
        Assert.Equal("New", result.Item!.Name);
    }

    [Fact]
    public async Task Reactivate_restores_a_deactivated_item()
    {
        DocumentItemStore store = Store();
        Guid clientId = fixture.ClientId;
        Item i = await store.CreateAsync(clientId, Body(sku: "A4"), default);
        await store.DeactivateAsync(clientId, i.Id, default);

        Assert.Equal(ReactivateResult.Reactivated, await store.ReactivateAsync(clientId, i.Id, default));
        // Now editable again.
        UpdateResult upd = await store.UpdateAsync(clientId, i.Id, Body(sku: "A4", name: "Edited"), default);
        Assert.Equal(UpdateOutcome.Updated, upd.Outcome);
        // And back in the active list.
        PagedResponse<Item> active = await store.GetByClientPagedAsync(clientId, 0, 50, true, false, default);
        Assert.Contains(active.Items, x => x.Id == i.Id);
    }

    [Fact]
    public async Task Reactivate_round_trips_the_item()
    {
        DocumentItemStore store = Store();
        Guid clientId = fixture.ClientId;
        Item i = await store.CreateAsync(clientId, Body(sku: "A5"), default);
        await store.DeactivateAsync(clientId, i.Id, default);
        await store.ReactivateAsync(clientId, i.Id, default);

        Item? got = await store.GetAsync(clientId, i.Id, default);
        Assert.NotNull(got);
        // Valuation is derived on read; the store layer returns it as 0.
        Assert.Equal(0m, got!.OnHandQuantity);
        Assert.Equal(0m, got.TotalValue);
    }

    [Fact]
    public async Task Reactivate_an_active_item_returns_AlreadyActive()
    {
        DocumentItemStore store = Store();
        Guid clientId = fixture.ClientId;
        Item i = await store.CreateAsync(clientId, Body(sku: "A6"), default);
        Assert.Equal(ReactivateResult.AlreadyActive, await store.ReactivateAsync(clientId, i.Id, default));
    }

    [Fact]
    public async Task Reactivate_missing_item_returns_NotFound()
    {
        DocumentItemStore store = Store();
        Guid clientId = fixture.ClientId;
        Assert.Equal(ReactivateResult.NotFound, await store.ReactivateAsync(clientId, Guid.NewGuid(), default));
    }
}
