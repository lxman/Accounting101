using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets;

/// <summary>The depreciation recipe: one run composes into one balanced two-line journal entry
/// (Dr Depreciation Expense / Cr Accumulated Depreciation) for the run total. Pure — leaving sequencing,
/// approval, and persistence to the engine.</summary>
public static class FixedAssetsPosting
{
    public const string DepreciationRunSourceType = "DepreciationRun";

    /// <summary>Composes the two-line entry for a depreciation run. Throws <see cref="ArgumentException"/>
    /// when the total is not positive (the run service guards against empty/zero runs upstream).</summary>
    public static PostEntryRequest ComposeDepreciationRun(
        Guid runId, decimal total, DateOnly effectiveDate, string? memo, FixedAssetsPostingAccounts accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        if (total <= 0m)
            throw new ArgumentException("Depreciation run total must be positive.", nameof(total));

        List<PostLineRequest> lines =
        [
            new(accounts.DepreciationExpenseAccountId,     "Debit",  total),
            new(accounts.AccumulatedDepreciationAccountId, "Credit", total),
        ];

        return new PostEntryRequest(
            Id: EntryIdentity.ForSource(DepreciationRunSourceType, runId),
            EffectiveDate: effectiveDate,
            Reference: null,
            Memo: memo,
            Lines: lines,
            SourceRef: runId,
            SourceType: DepreciationRunSourceType);
    }
}
