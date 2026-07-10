using Accounting101.Ledger.Contracts;
using Xunit;

namespace Accounting101.Inventory.Tests;

public class ItemValuationServiceTests
{
    private static readonly Guid Client = Guid.NewGuid();

    private static (ItemValuationService svc, InMemoryStockMovementStore movements, FakeLedgerClient ledger, FixedInventoryAccountsProvider accounts) Build()
    {
        var movements = new InMemoryStockMovementStore();
        var ledger = new FakeLedgerClient();
        var accounts = new FixedInventoryAccountsProvider();
        return (new ItemValuationService(movements, accounts, ledger), movements, ledger, accounts);
    }

    // Records a movement doc AND posts its dimensioned entry, mirroring what the movement service will do.
    private static async Task Post(InMemoryStockMovementStore movements, FakeLedgerClient ledger, FixedInventoryAccountsProvider acct,
        Guid itemId, MovementType type, decimal qty, decimal ext)
    {
        StockMovement m = await movements.RecordAsync(Client, new StockMovementBody(
            itemId, type, new DateOnly(2026, 1, 1), null, qty, ext / Math.Abs(qty), ext));
        await ledger.PostAsync(Client, InventoryPosting.Compose(type, qty, m.Id, itemId, ext, new DateOnly(2026, 1, 1), null,
            new InventoryPostingAccounts
            {
                InventoryAssetAccountId = acct.InventoryAssetAccountId, CogsAccountId = acct.CogsAccountId,
                GrniClearingAccountId = acct.GrniClearingAccountId, InventoryAdjustmentAccountId = acct.InventoryAdjustmentAccountId,
            }));
    }

    [Fact]
    public async Task Posted_receipt_folds_positive_value_and_projects_quantity()
    {
        (ItemValuationService svc, InMemoryStockMovementStore movements, FakeLedgerClient ledger, FixedInventoryAccountsProvider acct) = Build();
        Guid item = Guid.NewGuid();
        await Post(movements, ledger, acct, item, MovementType.Receipt, 10m, 100m);
        ledger.ApproveAll();

        ItemValuation v = await svc.GetAsync(Client, item, includePending: false);
        Assert.Equal(10m, v.OnHand);           // projected signed quantity
        Assert.Equal(100m, v.TotalValue);      // debit-normal asset → POSITIVE, no negation
        Assert.Equal(10m, v.AverageUnitCost);
    }

    [Fact]
    public async Task Pending_movement_is_posted_only_invisible_but_write_visible()
    {
        (ItemValuationService svc, InMemoryStockMovementStore movements, FakeLedgerClient ledger, FixedInventoryAccountsProvider acct) = Build();
        Guid item = Guid.NewGuid();
        await Post(movements, ledger, acct, item, MovementType.Receipt, 10m, 100m);   // left PendingApproval

        Assert.Equal(0m, (await svc.GetAsync(Client, item, includePending: false)).OnHand);
        ItemValuation write = await svc.GetAsync(Client, item, includePending: true);
        Assert.Equal(10m, write.OnHand);
        Assert.Equal(100m, write.TotalValue);
    }

    /// <summary>Pins the reversal/void coherence invariant that ItemValuationService.OnBooks exists to
    /// enforce — the property the whole ledger-first redesign turns on, and one that was already gotten
    /// wrong once (an earlier, ungated "Any(e => e.ReversalOf == primary.Id)" reversal check would drop the
    /// quantity as soon as ANY reversal exists, regardless of whether that reversal is itself on the books
    /// under the same gate — decoupling value and quantity). Value and quantity must always move together.</summary>
    [Fact]
    public async Task Reversal_gates_on_posting_keeping_value_and_quantity_coherent()
    {
        (ItemValuationService svc, InMemoryStockMovementStore movements, FakeLedgerClient ledger, FixedInventoryAccountsProvider acct) = Build();
        Guid item = Guid.NewGuid();

        // Post inline (rather than via the Post() helper) so we capture the entry id needed for ReverseAsync.
        StockMovement m = await movements.RecordAsync(Client, new StockMovementBody(
            item, MovementType.Receipt, new DateOnly(2026, 1, 1), null, 10m, 10m, 100m));
        PostEntryResponse posted = await ledger.PostAsync(Client, InventoryPosting.Compose(
            MovementType.Receipt, 10m, m.Id, item, 100m, new DateOnly(2026, 1, 1), null,
            new InventoryPostingAccounts
            {
                InventoryAssetAccountId = acct.InventoryAssetAccountId, CogsAccountId = acct.CogsAccountId,
                GrniClearingAccountId = acct.GrniClearingAccountId, InventoryAdjustmentAccountId = acct.InventoryAdjustmentAccountId,
            }));
        ledger.ApproveAll();   // primary entry is now Posted — on the books under BOTH gates

        // 1. Before reversal: posted-only read sees the receipt.
        ItemValuation before = await svc.GetAsync(Client, item, includePending: false);
        Assert.Equal(10m, before.OnHand);
        Assert.Equal(100m, before.TotalValue);

        await ledger.ReverseAsync(Client, posted.Id, new ReverseRequest(new DateOnly(2026, 1, 2), "test"));
        // The reversal is created PendingApproval — NOT yet on the books under the posted-only gate.

        // 2a. Posted-only gate: the pending reversal must NOT count, so the original movement is still on
        // the books. THIS is the guard against reintroducing an ungated reversal check — an ungated
        // "Any(e => e.ReversalOf == primary.Id)" would drop OnHand to 0 here while the posted-only value
        // fold (which also does not count the pending reversal's lines) still reads 100 — decoupling value
        // and quantity. The gated check keeps both at their pre-reversal values.
        ItemValuation postedOnlyAfterReversal = await svc.GetAsync(Client, item, includePending: false);
        Assert.Equal(10m, postedOnlyAfterReversal.OnHand);
        Assert.Equal(100m, postedOnlyAfterReversal.TotalValue);

        // 2b. Pending-inclusive gate: the reversal DOES count under this gate, so it folds away the
        // movement — value and quantity drop TOGETHER.
        ItemValuation pendingInclusiveAfterReversal = await svc.GetAsync(Client, item, includePending: true);
        Assert.Equal(0m, pendingInclusiveAfterReversal.OnHand);
        Assert.Equal(0m, pendingInclusiveAfterReversal.TotalValue);

        ledger.ApproveAll();   // reversal is now Posted too — on the books under the posted-only gate

        // 3. Posted-only gate: now the reversal counts, so the movement is fully rolled back — both zero.
        ItemValuation postedOnlyAfterApprove = await svc.GetAsync(Client, item, includePending: false);
        Assert.Equal(0m, postedOnlyAfterApprove.OnHand);
        Assert.Equal(0m, postedOnlyAfterApprove.TotalValue);
    }

    [Fact]
    public async Task Empty_item_folds_to_zero()
    {
        (ItemValuationService svc, _, _, _) = Build();
        ItemValuation v = await svc.GetAsync(Client, Guid.NewGuid(), includePending: false);
        Assert.Equal(0m, v.OnHand);
        Assert.Equal(0m, v.TotalValue);
        Assert.Equal(0m, v.AverageUnitCost);
    }

    /// <summary>The batch path (GetManyAsync, used by ListItems) must agree with the single-item path
    /// (GetAsync) for every item in the page, including an item with no movements at all.</summary>
    [Fact]
    public async Task GetManyAsync_agrees_with_GetAsync_for_each_item()
    {
        (ItemValuationService svc, InMemoryStockMovementStore movements, FakeLedgerClient ledger, FixedInventoryAccountsProvider acct) = Build();
        Guid itemA = Guid.NewGuid();
        Guid itemB = Guid.NewGuid();
        Guid itemNoMovements = Guid.NewGuid();

        await Post(movements, ledger, acct, itemA, MovementType.Receipt, 10m, 100m);
        await Post(movements, ledger, acct, itemB, MovementType.Receipt, 4m, 60m);
        ledger.ApproveAll();

        ItemValuation expectedA = await svc.GetAsync(Client, itemA, includePending: false);
        ItemValuation expectedB = await svc.GetAsync(Client, itemB, includePending: false);

        IReadOnlyDictionary<Guid, ItemValuation> many = await svc.GetManyAsync(
            Client, [itemA, itemB, itemNoMovements], includePending: false);

        Assert.Equal(expectedA.OnHand, many[itemA].OnHand);
        Assert.Equal(expectedA.TotalValue, many[itemA].TotalValue);
        Assert.Equal(expectedA.AverageUnitCost, many[itemA].AverageUnitCost);

        Assert.Equal(expectedB.OnHand, many[itemB].OnHand);
        Assert.Equal(expectedB.TotalValue, many[itemB].TotalValue);
        Assert.Equal(expectedB.AverageUnitCost, many[itemB].AverageUnitCost);

        Assert.Equal(0m, many[itemNoMovements].OnHand);
        Assert.Equal(0m, many[itemNoMovements].TotalValue);
    }

    /// <summary>Proves the ListItems performance fix: GetManyAsync makes a CONSTANT number of ledger calls
    /// (one subledger fold, at most one batched entry-status read) no matter how many items are in the page
    /// — not the N+1 shape of calling GetAsync once per item.</summary>
    [Fact]
    public async Task GetManyAsync_makes_a_constant_number_of_ledger_calls()
    {
        (ItemValuationService svc, InMemoryStockMovementStore movements, FakeLedgerClient ledger, FixedInventoryAccountsProvider acct) = Build();
        Guid itemA = Guid.NewGuid();
        Guid itemB = Guid.NewGuid();
        Guid itemC = Guid.NewGuid();

        await Post(movements, ledger, acct, itemA, MovementType.Receipt, 10m, 100m);
        await Post(movements, ledger, acct, itemB, MovementType.Receipt, 4m, 60m);
        await Post(movements, ledger, acct, itemC, MovementType.Receipt, 2m, 20m);
        ledger.ApproveAll();

        await svc.GetManyAsync(Client, [itemA, itemB, itemC], includePending: false);

        Assert.Equal(1, ledger.GetSubledgerCallCount);
        Assert.True(ledger.GetEntriesBySourceRefsCallCount <= 1);
    }
}
