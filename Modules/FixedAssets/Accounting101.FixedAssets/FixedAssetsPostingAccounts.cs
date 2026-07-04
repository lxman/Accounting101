namespace Accounting101.FixedAssets;

/// <summary>The chart accounts the fixed-assets module posts to. The first two are used by depreciation
/// runs (FA-2); all six are used by disposals (FA-3). Supplied by configuration; no hardcoded numbers.</summary>
public sealed record FixedAssetsPostingAccounts
{
    /// <summary>Depreciation Expense — debited for a run total, and for a disposal's catch-up depreciation.</summary>
    public required Guid DepreciationExpenseAccountId { get; init; }

    /// <summary>Accumulated Depreciation (contra-asset) — credited by runs; debited on disposal to clear it.</summary>
    public required Guid AccumulatedDepreciationAccountId { get; init; }

    /// <summary>Fixed-asset cost account — credited on disposal to remove the asset's cost from the books.</summary>
    public required Guid AssetCostAccountId { get; init; }

    /// <summary>Cash / disposal proceeds — debited for sale proceeds (omitted on a zero-proceeds retirement).</summary>
    public required Guid DisposalProceedsAccountId { get; init; }

    /// <summary>Gain on Disposal — credited when proceeds exceed net book value.</summary>
    public required Guid GainOnDisposalAccountId { get; init; }

    /// <summary>Loss on Disposal — debited when net book value exceeds proceeds.</summary>
    public required Guid LossOnDisposalAccountId { get; init; }
}
