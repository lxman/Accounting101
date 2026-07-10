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
            itemId, type, new DateOnly(2026, 1, 1), null, qty, ext / Math.Abs(qty), ext, 0m, 0m));
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

    [Fact]
    public async Task Empty_item_folds_to_zero()
    {
        (ItemValuationService svc, _, _, _) = Build();
        ItemValuation v = await svc.GetAsync(Client, Guid.NewGuid(), includePending: false);
        Assert.Equal(0m, v.OnHand);
        Assert.Equal(0m, v.TotalValue);
        Assert.Equal(0m, v.AverageUnitCost);
    }
}
