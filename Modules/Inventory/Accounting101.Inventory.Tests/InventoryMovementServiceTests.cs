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
        ItemValuationService valuation = new(movements, accounts, ledger);
        InventoryMovementService service = new(items, movements, accounts, ledger, valuation);
        return (service, items, movements, ledger, accounts);
    }

    /// <summary>Pins the T5 cut-over: the record path computes the effect from the pending-inclusive fold
    /// (ItemValuationService), NOT the stored item's OnHandQuantity/TotalValue. Both receipts are posted
    /// (left PendingApproval, never approved) so a fold that only counted posted entries would see zero —
    /// the fake counts pending entries under includePending:true, proving the write path uses that gate.</summary>
    [Fact]
    public async Task Issue_costs_at_the_folded_weighted_average()
    {
        (InventoryMovementService svc, InMemoryItemStore items, InMemoryStockMovementStore movements, FakeLedgerClient ledger, _) = Build();
        Guid clientId = Guid.NewGuid();
        Item item = await items.CreateAsync(clientId, new ItemBody("SKU1", "Widget", null, "ea"));

        await svc.RecordAsync(clientId, new RecordMovement(item.Id, MovementType.Receipt, 10m, 10m, new DateOnly(2026, 1, 1), null));
        await svc.RecordAsync(clientId, new RecordMovement(item.Id, MovementType.Receipt, 10m, 20m, new DateOnly(2026, 1, 2), null));
        // On-hand 20 @ avg 15. Issue 4 → COGS 60.
        StockMovement issue = await svc.RecordAsync(clientId, new RecordMovement(item.Id, MovementType.Issue, 4m, null, new DateOnly(2026, 1, 3), null));

        Assert.Equal(60m, issue.ExtendedCost);
        Assert.Equal(15m, issue.AppliedUnitCost);
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

    /// <summary>A zero-cost receipt passes the valuation guards (which only reject a NEGATIVE unit cost)
    /// but produces a zero extended cost, which InventoryPosting.Compose rejects. That rejection must
    /// happen BEFORE any side effect — otherwise the movement would be persisted and the item's on-hand
    /// mutated with no GL entry ever posted for it (a stranded movement).</summary>
    [Fact]
    public async Task Receipt_with_zero_unit_cost_throws_before_any_side_effect()
    {
        (InventoryMovementService svc, InMemoryItemStore items, InMemoryStockMovementStore movements, FakeLedgerClient ledger, _) = Build();
        Item item = await items.CreateAsync(Client, new ItemBody("SKU1", "Widget", null, "each"));

        await Assert.ThrowsAsync<ArgumentException>(() => svc.RecordAsync(Client,
            new RecordMovement(item.Id, MovementType.Receipt, 10m, 0m, new DateOnly(2026, 1, 15), null)));

        Item after = (await items.GetAsync(Client, item.Id))!;
        Assert.Equal(0m, after.OnHandQuantity);
        Assert.Equal(0m, after.TotalValue);

        PagedResponse<StockMovement> paged = await movements.GetByItemPagedAsync(Client, item.Id, 0, 200, true, true);
        Assert.Empty(paged.Items);

        Assert.Empty(ledger.Posted);
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

    private static DateOnly D(int year, int month, int day) => new(year, month, day);

    /// <summary>Voiding the latest movement for an item reverses (or withdraws) its spawned entry,
    /// restores the item's pre-movement valuation, and flips the movement's own Status to Void.</summary>
    [Fact]
    public async Task Void_latest_restores_valuation_and_reverses_entry()
    {
        (var svc, var items, var movements, var ledger, _) = Build();
        Item item = await items.CreateAsync(Client, new ItemBody("SKU1", "Widget", null, "each"));
        await svc.RecordAsync(Client, new RecordMovement(item.Id, MovementType.Receipt, 10m, 2m, D(2026, 1, 10), null));
        StockMovement issue = await svc.RecordAsync(Client, new RecordMovement(item.Id, MovementType.Issue, 4m, null, D(2026, 1, 20), null));
        // item now (6, 12). Void the issue → back to (10, 20).
        StockMovement voided = await svc.VoidAsync(Client, issue.Id, "oops");
        Item after = (await items.GetAsync(Client, item.Id))!;
        Assert.Equal(10m, after.OnHandQuantity);
        Assert.Equal(20m, after.TotalValue);
        Assert.True(ledger.ReversedOrWithdrawn);
        Assert.Equal(MovementStatus.Void, voided.Status);
    }

    /// <summary>LIFO enforcement: only the most-recent non-voided movement for an item may be voided —
    /// attempting to void an earlier movement while a later one still stands is rejected.</summary>
    [Fact]
    public async Task Void_of_non_latest_movement_throws_InvalidOperationException()
    {
        (InventoryMovementService svc, InMemoryItemStore items, _, _, _) = Build();
        Item item = await items.CreateAsync(Client, new ItemBody("SKU1", "Widget", null, "each"));
        StockMovement receipt = await svc.RecordAsync(Client,
            new RecordMovement(item.Id, MovementType.Receipt, 10m, 2m, D(2026, 1, 10), null));
        await svc.RecordAsync(Client, new RecordMovement(item.Id, MovementType.Issue, 4m, null, D(2026, 1, 20), null));

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.VoidAsync(Client, receipt.Id, null));
    }

    [Fact]
    public async Task Void_of_already_void_movement_throws_InvalidOperationException()
    {
        (InventoryMovementService svc, InMemoryItemStore items, _, _, _) = Build();
        Item item = await items.CreateAsync(Client, new ItemBody("SKU1", "Widget", null, "each"));
        StockMovement receipt = await svc.RecordAsync(Client,
            new RecordMovement(item.Id, MovementType.Receipt, 10m, 2m, D(2026, 1, 10), null));
        await svc.VoidAsync(Client, receipt.Id, null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.VoidAsync(Client, receipt.Id, null));
    }

    [Fact]
    public async Task Void_of_missing_movement_throws_KeyNotFoundException()
    {
        (InventoryMovementService svc, _, _, _, _) = Build();

        await Assert.ThrowsAsync<KeyNotFoundException>(() => svc.VoidAsync(Client, Guid.NewGuid(), null));
    }
}
