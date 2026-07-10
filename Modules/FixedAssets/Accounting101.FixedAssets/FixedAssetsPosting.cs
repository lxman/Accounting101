using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets;

/// <summary>The depreciation recipe: one run composes into one balanced journal entry — one aggregate
/// Dr Depreciation Expense line for the run total, and one Cr Accumulated Depreciation line per asset,
/// each dimensioned <c>{Asset=line.AssetId}</c> so the per-asset accumulated depreciation is directly
/// derivable from the GL. Pure — leaving sequencing, approval, and persistence to the engine.</summary>
public static class FixedAssetsPosting
{
    public const string DepreciationRunSourceType = "DepreciationRun";

    /// <summary>Composes the entry for a depreciation run: one aggregate expense debit plus one
    /// per-asset accumulated-depreciation credit. Throws <see cref="ArgumentException"/> when the total
    /// is not positive (the run service guards against empty/zero runs upstream).</summary>
    public static PostEntryRequest ComposeDepreciationRun(
        Guid runId, IReadOnlyList<DepreciationRunLine> lines, decimal total, DateOnly effectiveDate, string? memo,
        FixedAssetsPostingAccounts accounts)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(accounts);
        if (total <= 0m)
            throw new ArgumentException("Depreciation run total must be positive.", nameof(total));

        List<PostLineRequest> entryLines =
            [new(accounts.DepreciationExpenseAccountId, "Debit", total)];
        entryLines.AddRange(lines.Select(l =>
            new PostLineRequest(accounts.AccumulatedDepreciationAccountId, "Credit", l.Amount,
                new Dictionary<string, Guid> { ["Asset"] = l.AssetId })));

        return new PostEntryRequest(
            Id: EntryIdentity.ForSource(DepreciationRunSourceType, runId),
            EffectiveDate: effectiveDate,
            Reference: null,
            Memo: memo,
            Lines: entryLines,
            SourceRef: runId,
            SourceType: DepreciationRunSourceType);
    }
}
