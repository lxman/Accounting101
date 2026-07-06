namespace Accounting101.Inventory;

/// <summary>The chart accounts the inventory module posts to for stock movements. Supplied by configuration; no hardcoded numbers.</summary>
public sealed record InventoryPostingAccounts
{
    /// <summary>Inventory Asset account — debited for receipts and overages, credited for issues and shrinkage.</summary>
    public required Guid InventoryAssetAccountId { get; init; }

    /// <summary>Cost of Goods Sold account — debited for issues.</summary>
    public required Guid CogsAccountId { get; init; }

    /// <summary>Goods Received Not Invoiced (GRNI) Clearing account — credited for receipts.</summary>
    public required Guid GrniClearingAccountId { get; init; }

    /// <summary>Inventory Adjustment account — debited for shrinkage, credited for overages.</summary>
    public required Guid InventoryAdjustmentAccountId { get; init; }
}
