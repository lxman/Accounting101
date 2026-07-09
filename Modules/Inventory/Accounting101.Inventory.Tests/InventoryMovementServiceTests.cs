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

    /// <summary>Issue costs at the item's current average — no unit cost is supplied. Drives a (20,60)
    /// item down through a partial issue of 5 (COGS 15, item becomes (15,45)) and then a full issue of
    /// the remaining 15 (COGS 45, item clears to exactly (0,0) — no rounding residue). Each issue posts
    /// its own balanced Dr COGS / Cr Inventory entry for its own extended cost.</summary>
    [Fact]
    public async Task Issue_costs_at_average_and_posts_cogs_debit_and_inventory_credit()
    {
        (InventoryMovementService svc, InMemoryItemStore items, InMemoryStockMovementStore _, FakeLedgerClient ledger, FixedInventoryAccountsProvider accts) = Build();
        Item item = await items.CreateAsync(Client, new ItemBody("SKU1", "Widget", null, "each"));
        await svc.RecordAsync(Client,
            new RecordMovement(item.Id, MovementType.Receipt, 20m, 3m, new DateOnly(2026, 1, 1), null));

        StockMovement issue1 = await svc.RecordAsync(Client,
            new RecordMovement(item.Id, MovementType.Issue, 5m, null, new DateOnly(2026, 1, 15), null));

        Assert.Equal(3m, issue1.AppliedUnitCost);
        Assert.Equal(15m, issue1.ExtendedCost);
        Item afterFirst = (await items.GetAsync(Client, item.Id))!;
        Assert.Equal(15m, afterFirst.OnHandQuantity);
        Assert.Equal(45m, afterFirst.TotalValue);

        Assert.Equal(2, ledger.Posted.Count);
        PostEntryRequest firstIssueEntry = ledger.Posted[1];
        Assert.Contains(firstIssueEntry.Lines, l => l.AccountId == accts.CogsAccountId && l.Direction == "Debit" && l.Amount == 15m);
        Assert.Contains(firstIssueEntry.Lines, l => l.AccountId == accts.InventoryAssetAccountId && l.Direction == "Credit" && l.Amount == 15m);

        StockMovement issue2 = await svc.RecordAsync(Client,
            new RecordMovement(item.Id, MovementType.Issue, 15m, null, new DateOnly(2026, 1, 20), null));

        Assert.Equal(3m, issue2.AppliedUnitCost);
        Assert.Equal(45m, issue2.ExtendedCost);
        Item afterSecond = (await items.GetAsync(Client, item.Id))!;
        Assert.Equal(0m, afterSecond.OnHandQuantity);
        Assert.Equal(0m, afterSecond.TotalValue);

        Assert.Equal(3, ledger.Posted.Count);
        PostEntryRequest secondIssueEntry = ledger.Posted[2];
        Assert.Contains(secondIssueEntry.Lines, l => l.AccountId == accts.CogsAccountId && l.Direction == "Debit" && l.Amount == 45m);
        Assert.Contains(secondIssueEntry.Lines, l => l.AccountId == accts.InventoryAssetAccountId && l.Direction == "Credit" && l.Amount == 45m);
    }

    [Fact]
    public async Task Issue_exceeding_on_hand_throws_InvalidOperationException()
    {
        (InventoryMovementService svc, InMemoryItemStore items, _, _, _) = Build();
        Item item = await items.CreateAsync(Client, new ItemBody("SKU1", "Widget", null, "each"));
        await svc.RecordAsync(Client,
            new RecordMovement(item.Id, MovementType.Receipt, 20m, 3m, new DateOnly(2026, 1, 1), null));

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RecordAsync(Client,
            new RecordMovement(item.Id, MovementType.Issue, 999m, null, new DateOnly(2026, 1, 15), null)));
    }

    /// <summary>Overage (positive signed quantity) behaves like a receipt at the supplied unit cost: a
    /// (10,30) item (avg $3) taking a +5 @ $4 overage becomes (15,50) and posts Dr Inventory / Cr
    /// InventoryAdjustment for the extended cost.</summary>
    [Fact]
    public async Task Overage_adjustment_costs_at_supplied_unit_cost_and_posts_inventory_debit()
    {
        (InventoryMovementService svc, InMemoryItemStore items, InMemoryStockMovementStore _, FakeLedgerClient ledger, FixedInventoryAccountsProvider accts) = Build();
        Item item = await items.CreateAsync(Client, new ItemBody("SKU1", "Widget", null, "each"));
        await svc.RecordAsync(Client,
            new RecordMovement(item.Id, MovementType.Receipt, 10m, 3m, new DateOnly(2026, 1, 1), null));

        StockMovement mv = await svc.RecordAsync(Client,
            new RecordMovement(item.Id, MovementType.Adjustment, 5m, 4m, new DateOnly(2026, 1, 15), "Cycle count overage"));

        Assert.Equal(4m, mv.AppliedUnitCost);
        Assert.Equal(20m, mv.ExtendedCost);
        Item after = (await items.GetAsync(Client, item.Id))!;
        Assert.Equal(15m, after.OnHandQuantity);
        Assert.Equal(50m, after.TotalValue);

        Assert.Equal(2, ledger.Posted.Count);
        PostEntryRequest posted = ledger.Posted[1];
        Assert.Contains(posted.Lines, l => l.AccountId == accts.InventoryAssetAccountId && l.Direction == "Debit" && l.Amount == 20m);
        Assert.Contains(posted.Lines, l => l.AccountId == accts.InventoryAdjustmentAccountId && l.Direction == "Credit" && l.Amount == 20m);
    }

    /// <summary>Shrinkage (negative signed quantity) costs at the item's current average — no unit cost is
    /// supplied. A (10,30) item (avg $3) taking a -4 shrinkage becomes (6,18) and posts Dr
    /// InventoryAdjustment / Cr Inventory for the extended cost.</summary>
    [Fact]
    public async Task Shrinkage_adjustment_costs_at_average_and_posts_inventory_credit()
    {
        (InventoryMovementService svc, InMemoryItemStore items, InMemoryStockMovementStore _, FakeLedgerClient ledger, FixedInventoryAccountsProvider accts) = Build();
        Item item = await items.CreateAsync(Client, new ItemBody("SKU1", "Widget", null, "each"));
        await svc.RecordAsync(Client,
            new RecordMovement(item.Id, MovementType.Receipt, 10m, 3m, new DateOnly(2026, 1, 1), null));

        StockMovement mv = await svc.RecordAsync(Client,
            new RecordMovement(item.Id, MovementType.Adjustment, -4m, null, new DateOnly(2026, 1, 15), "Cycle count shrinkage"));

        Assert.Equal(3m, mv.AppliedUnitCost);
        Assert.Equal(12m, mv.ExtendedCost);
        Item after = (await items.GetAsync(Client, item.Id))!;
        Assert.Equal(6m, after.OnHandQuantity);
        Assert.Equal(18m, after.TotalValue);

        Assert.Equal(2, ledger.Posted.Count);
        PostEntryRequest posted = ledger.Posted[1];
        Assert.Contains(posted.Lines, l => l.AccountId == accts.InventoryAdjustmentAccountId && l.Direction == "Debit" && l.Amount == 12m);
        Assert.Contains(posted.Lines, l => l.AccountId == accts.InventoryAssetAccountId && l.Direction == "Credit" && l.Amount == 12m);
    }

    [Fact]
    public async Task Shrinkage_beyond_on_hand_throws_InvalidOperationException()
    {
        (InventoryMovementService svc, InMemoryItemStore items, _, _, _) = Build();
        Item item = await items.CreateAsync(Client, new ItemBody("SKU1", "Widget", null, "each"));
        await svc.RecordAsync(Client,
            new RecordMovement(item.Id, MovementType.Receipt, 10m, 3m, new DateOnly(2026, 1, 1), null));

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RecordAsync(Client,
            new RecordMovement(item.Id, MovementType.Adjustment, -999m, null, new DateOnly(2026, 1, 15), null)));
    }

    [Fact]
    public async Task Overage_missing_unit_cost_throws_ArgumentException()
    {
        (InventoryMovementService svc, InMemoryItemStore items, _, _, _) = Build();
        Item item = await items.CreateAsync(Client, new ItemBody("SKU1", "Widget", null, "each"));
        await svc.RecordAsync(Client,
            new RecordMovement(item.Id, MovementType.Receipt, 10m, 3m, new DateOnly(2026, 1, 1), null));

        await Assert.ThrowsAsync<ArgumentException>(() => svc.RecordAsync(Client,
            new RecordMovement(item.Id, MovementType.Adjustment, 5m, null, new DateOnly(2026, 1, 15), null)));
    }
}
