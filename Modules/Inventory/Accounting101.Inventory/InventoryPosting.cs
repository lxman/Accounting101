using Accounting101.Ledger.Contracts;

namespace Accounting101.Inventory;

/// <summary>The four balanced GL recipes for stock movements: receipt, issue, and adjustment (shrinkage/overage).
/// Each recipe composes into one balanced two-line journal entry. Pure — leaving sequencing, approval, and persistence to the engine.</summary>
public static class InventoryPosting
{
    public const string StockMovementSourceType = "StockMovement";

    /// <summary>Composes the two-line entry for a stock movement. Throws <see cref="ArgumentException"/>
    /// when the extended cost is not positive.</summary>
    public static PostEntryRequest Compose(
        MovementType type, decimal signedQuantity, Guid movementId, Guid itemId, decimal extendedCost,
        DateOnly effectiveDate, string? memo, InventoryPostingAccounts accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        if (extendedCost <= 0m)
            throw new ArgumentException("Extended cost must be positive.", nameof(extendedCost));

        Dictionary<string, Guid> itemDim = new() { ["Item"] = itemId };

        List<PostLineRequest> lines = type switch
        {
            MovementType.Receipt =>
            [
                new(accounts.InventoryAssetAccountId, "Debit", extendedCost, itemDim),
                new(accounts.GrniClearingAccountId, "Credit", extendedCost),
            ],
            MovementType.Issue =>
            [
                new(accounts.CogsAccountId, "Debit", extendedCost),
                new(accounts.InventoryAssetAccountId, "Credit", extendedCost, itemDim),
            ],
            MovementType.Adjustment when signedQuantity < 0m =>   // shrinkage
            [
                new(accounts.InventoryAdjustmentAccountId, "Debit", extendedCost),
                new(accounts.InventoryAssetAccountId, "Credit", extendedCost, itemDim),
            ],
            MovementType.Adjustment =>                            // overage
            [
                new(accounts.InventoryAssetAccountId, "Debit", extendedCost, itemDim),
                new(accounts.InventoryAdjustmentAccountId, "Credit", extendedCost),
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };

        return new PostEntryRequest(
            Id: EntryIdentity.ForSource(StockMovementSourceType, movementId),
            EffectiveDate: effectiveDate,
            Reference: null,
            Memo: memo,
            Lines: lines,
            SourceRef: movementId,
            SourceType: StockMovementSourceType);
    }
}
