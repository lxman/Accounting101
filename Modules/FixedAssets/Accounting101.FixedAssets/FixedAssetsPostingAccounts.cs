namespace Accounting101.FixedAssets;

/// <summary>The two chart accounts a depreciation run posts to. Supplied by configuration; no hardcoded
/// account numbers.</summary>
public sealed record FixedAssetsPostingAccounts
{
    /// <summary>Depreciation Expense — debited for the run total.</summary>
    public required Guid DepreciationExpenseAccountId { get; init; }

    /// <summary>Accumulated Depreciation (contra-asset) — credited for the run total.</summary>
    public required Guid AccumulatedDepreciationAccountId { get; init; }
}
