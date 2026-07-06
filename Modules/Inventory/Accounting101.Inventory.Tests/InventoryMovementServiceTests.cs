using Accounting101.Ledger.Contracts;

namespace Accounting101.Inventory.Tests;

/// <summary>Proves the movement service's receipt path: it re-blends the item's valuation, persists a
/// numbered movement, and posts exactly one balanced Dr Inventory / Cr GRNI entry for the extended cost.
/// Also proves the guard rails: unknown item, inactive item, non-positive receipt quantity, and a receipt
/// missing its unit cost all fail before any side effect.</summary>
public sealed class InventoryMovementServiceTests
{
    private static readonly Guid Client = Guid.NewGuid();

    private static (InventoryMovementService Service, InMemoryItemStore Items, InMemoryStockMovementStore Movements,
        FakeLedgerClient Ledger, FixedInventoryAccountsProvider Accounts) Build()
    {
        InMemoryItemStore items = new();
        InMemoryStockMovementStore movements = new();
        FakeLedgerClient ledger = new();
        FixedInventoryAccountsProvider accounts = new();
        InventoryMovementService service = new(items, movements, accounts, ledger);
        return (service, items, movements, ledger, accounts);
    }

    [Fact]
    public async Task Receipt_updates_item_and_posts_inventory_debit()
    {
        (InventoryMovementService svc, InMemoryItemStore items, InMemoryStockMovementStore _, FakeLedgerClient ledger, FixedInventoryAccountsProvider accts) = Build();
        Item item = await items.CreateAsync(Client, new ItemBody("SKU1", "Widget", null, "each"));

        StockMovement mv = await svc.RecordAsync(Client,
            new RecordMovement(item.Id, MovementType.Receipt, 10m, 2m, new DateOnly(2026, 1, 15), null));

        Assert.NotNull(mv.Number);
        Item after = (await items.GetAsync(Client, item.Id))!;
        Assert.Equal(10m, after.OnHandQuantity);
        Assert.Equal(20m, after.TotalValue);

        PostEntryRequest posted = Assert.Single(ledger.Posted);
        Assert.Contains(posted.Lines, l => l.AccountId == accts.InventoryAssetAccountId && l.Direction == "Debit" && l.Amount == 20m);
        Assert.Contains(posted.Lines, l => l.AccountId == accts.GrniClearingAccountId && l.Direction == "Credit" && l.Amount == 20m);
    }

    [Fact]
    public async Task Receipt_for_unknown_item_throws_KeyNotFoundException()
    {
        (InventoryMovementService svc, _, _, _, _) = Build();

        await Assert.ThrowsAsync<KeyNotFoundException>(() => svc.RecordAsync(Client,
            new RecordMovement(Guid.NewGuid(), MovementType.Receipt, 10m, 2m, new DateOnly(2026, 1, 15), null)));
    }

    [Fact]
    public async Task Receipt_for_inactive_item_throws_InvalidOperationException()
    {
        (InventoryMovementService svc, InMemoryItemStore items, _, _, _) = Build();
        Item item = await items.CreateAsync(Client, new ItemBody("SKU1", "Widget", null, "each"));
        items.ForceInactive(item.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RecordAsync(Client,
            new RecordMovement(item.Id, MovementType.Receipt, 10m, 2m, new DateOnly(2026, 1, 15), null)));
    }

    [Fact]
    public async Task Receipt_with_nonpositive_quantity_throws_ArgumentException()
    {
        (InventoryMovementService svc, InMemoryItemStore items, _, _, _) = Build();
        Item item = await items.CreateAsync(Client, new ItemBody("SKU1", "Widget", null, "each"));

        await Assert.ThrowsAsync<ArgumentException>(() => svc.RecordAsync(Client,
            new RecordMovement(item.Id, MovementType.Receipt, 0m, 2m, new DateOnly(2026, 1, 15), null)));
    }

    [Fact]
    public async Task Receipt_missing_unit_cost_throws_ArgumentException()
    {
        (InventoryMovementService svc, InMemoryItemStore items, _, _, _) = Build();
        Item item = await items.CreateAsync(Client, new ItemBody("SKU1", "Widget", null, "each"));

        await Assert.ThrowsAsync<ArgumentException>(() => svc.RecordAsync(Client,
            new RecordMovement(item.Id, MovementType.Receipt, 10m, null, new DateOnly(2026, 1, 15), null)));
    }
}
