using Accounting101.Ledger.Contracts;

namespace Accounting101.Inventory.Tests;

public sealed class ItemDocumentStoreTests(ItemDocumentStoreFixture fixture) : IClassFixture<ItemDocumentStoreFixture>
{
    private DocumentItemStore Store() => new(fixture.Store);

    private static ItemBody Body(string sku = "SKU1", string name = "Widget") =>
        new(sku, name, null, "each");

    [Fact]
    public async Task Create_stamps_active_status_and_zero_valuation()
    {
        Item item = await Store().CreateAsync(fixture.ClientId, Body());
        Assert.NotEqual(Guid.Empty, item.Id);
        Assert.Equal(ItemStatus.Active, item.Status);
        Assert.Equal(0m, item.OnHandQuantity);
        Assert.Equal(0m, item.TotalValue);
        Assert.Equal("Widget", item.Name);
    }

    [Fact]
    public async Task Get_returns_a_created_item()
    {
        Item created = await Store().CreateAsync(fixture.ClientId, Body(name: "Gadget"));
        Item? got = await Store().GetAsync(fixture.ClientId, created.Id);
        Assert.NotNull(got);
        Assert.Equal("Gadget", got!.Name);
    }

    [Fact]
    public async Task GetBySku_finds_by_sku()
    {
        Item created = await Store().CreateAsync(fixture.ClientId, Body(sku: "FIND-ME"));
        Item? got = await Store().GetBySkuAsync(fixture.ClientId, "FIND-ME");
        Assert.NotNull(got);
        Assert.Equal(created.Id, got!.Id);
    }

    [Fact]
    public async Task Update_changes_editable_params_and_preserves_valuation()
    {
        Item created = await Store().CreateAsync(fixture.ClientId, Body(name: "Old"));
        await Store().SetValuationAsync(fixture.ClientId, created.Id, 5m, 50m);

        UpdateResult result = await Store().UpdateAsync(fixture.ClientId, created.Id, Body(name: "New"));
        Assert.Equal(UpdateOutcome.Updated, result.Outcome);
        Item? updated = result.Item;
        Assert.NotNull(updated);
        Assert.Equal("New", updated!.Name);
        Assert.Equal(ItemStatus.Active, updated.Status);
        Assert.Equal(5m, updated.OnHandQuantity);
        Assert.Equal(50m, updated.TotalValue);
    }

    [Fact]
    public async Task Update_of_a_missing_item_returns_NotFound() =>
        Assert.Equal(UpdateOutcome.NotFound, (await Store().UpdateAsync(fixture.ClientId, Guid.NewGuid(), Body())).Outcome);

    [Fact]
    public async Task Update_with_a_duplicate_sku_returns_DuplicateSku()
    {
        Item first = await Store().CreateAsync(fixture.ClientId, Body(sku: "DUP-A"));
        Item second = await Store().CreateAsync(fixture.ClientId, Body(sku: "DUP-B"));

        UpdateResult result = await Store().UpdateAsync(fixture.ClientId, second.Id, Body(sku: "DUP-A"));
        Assert.Equal(UpdateOutcome.DuplicateSku, result.Outcome);
    }

    [Fact]
    public async Task Deactivate_removes_the_item_from_the_default_list_but_include_inactive_shows_it()
    {
        Item created = await Store().CreateAsync(fixture.ClientId, Body(sku: "RETIRE-ME"));

        DeactivateResult result = await Store().DeactivateAsync(fixture.ClientId, created.Id);
        Assert.Equal(DeactivateResult.Deactivated, result);

        PagedResponse<Item> active = await Store().GetByClientPagedAsync(fixture.ClientId, 0, 200, true, includeInactive: false, default);
        Assert.DoesNotContain(active.Items, i => i.Id == created.Id);

        PagedResponse<Item> all = await Store().GetByClientPagedAsync(fixture.ClientId, 0, 200, true, includeInactive: true, default);
        Assert.Contains(all.Items, i => i.Id == created.Id);
    }

    [Fact]
    public async Task Deactivate_is_not_found_then_conflict_on_repeat()
    {
        Assert.Equal(DeactivateResult.NotFound, await Store().DeactivateAsync(fixture.ClientId, Guid.NewGuid()));

        Item created = await Store().CreateAsync(fixture.ClientId, Body(sku: "ONCE"));
        Assert.Equal(DeactivateResult.Deactivated, await Store().DeactivateAsync(fixture.ClientId, created.Id));
        Assert.Equal(DeactivateResult.AlreadyInactive, await Store().DeactivateAsync(fixture.ClientId, created.Id));
    }

    [Fact]
    public async Task Deactivate_is_blocked_while_stock_on_hand()
    {
        Item item = await Store().CreateAsync(fixture.ClientId, Body(sku: "STOCKED"));
        await Store().SetValuationAsync(fixture.ClientId, item.Id, 5m, 50m);
        Assert.Equal(DeactivateResult.HasStock, await Store().DeactivateAsync(fixture.ClientId, item.Id));
    }

    [Fact]
    public async Task SetValuation_overwrites_on_hand_and_total_value()
    {
        Item item = await Store().CreateAsync(fixture.ClientId, Body(sku: "VAL"));
        await Store().SetValuationAsync(fixture.ClientId, item.Id, 10m, 100m);
        Item? got = await Store().GetAsync(fixture.ClientId, item.Id);
        Assert.Equal(10m, got!.OnHandQuantity);
        Assert.Equal(100m, got.TotalValue);

        await Store().SetValuationAsync(fixture.ClientId, item.Id, 3m, 30m);
        got = await Store().GetAsync(fixture.ClientId, item.Id);
        Assert.Equal(3m, got!.OnHandQuantity);
        Assert.Equal(30m, got.TotalValue);
    }
}
