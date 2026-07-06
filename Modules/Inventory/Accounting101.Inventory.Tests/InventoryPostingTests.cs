using Accounting101.Inventory;
using Accounting101.Ledger.Contracts;
using Xunit;

namespace Accounting101.Inventory.Tests;

public class InventoryPostingTests
{
    private static readonly InventoryPostingAccounts Accts = new()
    {
        InventoryAssetAccountId = Guid.NewGuid(),
        CogsAccountId = Guid.NewGuid(),
        GrniClearingAccountId = Guid.NewGuid(),
        InventoryAdjustmentAccountId = Guid.NewGuid(),
    };

    [Fact]
    public void Receipt_debits_inventory_credits_grni()
    {
        PostEntryRequest e = InventoryPosting.Compose(MovementType.Receipt, 10m, Guid.NewGuid(), 40m, new DateOnly(2026, 1, 31), null, Accts);
        Assert.Contains(e.Lines, l => l.AccountId == Accts.InventoryAssetAccountId && l.Direction == "Debit" && l.Amount == 40m);
        Assert.Contains(e.Lines, l => l.AccountId == Accts.GrniClearingAccountId && l.Direction == "Credit" && l.Amount == 40m);
    }

    [Fact]
    public void Issue_debits_cogs_credits_inventory()
    {
        PostEntryRequest e = InventoryPosting.Compose(MovementType.Issue, 5m, Guid.NewGuid(), 15m, new DateOnly(2026, 1, 31), null, Accts);
        Assert.Contains(e.Lines, l => l.AccountId == Accts.CogsAccountId && l.Direction == "Debit" && l.Amount == 15m);
        Assert.Contains(e.Lines, l => l.AccountId == Accts.InventoryAssetAccountId && l.Direction == "Credit" && l.Amount == 15m);
    }

    [Fact]
    public void Shrinkage_debits_adjustment_credits_inventory()
    {
        PostEntryRequest e = InventoryPosting.Compose(MovementType.Adjustment, -4m, Guid.NewGuid(), 12m, new DateOnly(2026, 1, 31), null, Accts);
        Assert.Contains(e.Lines, l => l.AccountId == Accts.InventoryAdjustmentAccountId && l.Direction == "Debit" && l.Amount == 12m);
        Assert.Contains(e.Lines, l => l.AccountId == Accts.InventoryAssetAccountId && l.Direction == "Credit" && l.Amount == 12m);
    }

    [Fact]
    public void Overage_debits_inventory_credits_adjustment()
    {
        PostEntryRequest e = InventoryPosting.Compose(MovementType.Adjustment, 5m, Guid.NewGuid(), 20m, new DateOnly(2026, 1, 31), null, Accts);
        Assert.Contains(e.Lines, l => l.AccountId == Accts.InventoryAssetAccountId && l.Direction == "Debit" && l.Amount == 20m);
        Assert.Contains(e.Lines, l => l.AccountId == Accts.InventoryAdjustmentAccountId && l.Direction == "Credit" && l.Amount == 20m);
    }

    [Fact]
    public void Source_backlink_is_set()
    {
        Guid mv = Guid.NewGuid();
        PostEntryRequest e = InventoryPosting.Compose(MovementType.Receipt, 10m, mv, 40m, new DateOnly(2026, 1, 31), "memo", Accts);
        Assert.Equal(mv, e.SourceRef);
        Assert.Equal(InventoryPosting.StockMovementSourceType, e.SourceType);
    }
}
