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
    public async Task Update_changes_editable_params()
    {
        Item created = await Store().CreateAsync(fixture.ClientId, Body(name: "Old"));

        UpdateResult result = await Store().UpdateAsync(fixture.ClientId, created.Id, Body(name: "New"));
        Assert.Equal(UpdateOutcome.Updated, result.Outcome);
        Item? updated = result.Item;
        Assert.NotNull(updated);
        Assert.Equal("New", updated!.Name);
        Assert.Equal(ItemStatus.Active, updated.Status);
        // Valuation is no longer stored on the document — the store returns it as 0 (the service overlays
        // the ledger fold on read), so the store-level Item carries the default 0/0.
        Assert.Equal(0m, updated.OnHandQuantity);
        Assert.Equal(0m, updated.TotalValue);
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

    /// <summary>The has-stock guard lives in InventoryService.DeactivateAsync, which reads the posted-only
    /// ledger fold (see InventoryServiceTests). The store carries no on-hand field at all now, so it
    /// deactivates unconditionally — no stored-valuation guard exists at this layer.</summary>
    [Fact]
    public async Task Deactivate_does_not_guard_on_any_stored_valuation()
    {
        Item item = await Store().CreateAsync(fixture.ClientId, Body(sku: "STOCKED"));
        Assert.Equal(DeactivateResult.Deactivated, await Store().DeactivateAsync(fixture.ClientId, item.Id));
    }
}
