using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets;

/// <summary>The disposal recipe: one asset removal composes into one balanced journal entry that clears
/// the asset's cost and accumulated depreciation, records any final catch-up depreciation and proceeds,
/// and books the gain or loss. Zero-amount lines are omitted (a disposal's shape varies by proceeds /
/// catch-up / gain-vs-loss). Pure — sequencing, approval, and persistence are the engine's.
/// <para>The Accumulated Depreciation line debits only <c>currentAccumulated</c>: the catch-up's own
/// contribution to accumulated depreciation and its immediate clearing on disposal net to zero, leaving
/// the depreciation expense in P&amp;L and only the pre-existing accumulated cleared.</para></summary>
public static class FixedAssetsDisposalPosting
{
    public const string DisposalSourceType = "Disposal";

    public static PostEntryRequest ComposeDisposal(
        Guid disposalId, DateOnly disposalDate, decimal acquisitionCost, decimal currentAccumulated,
        decimal catchUp, decimal proceeds, decimal gainLoss, string? memo, FixedAssetsPostingAccounts accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        if (acquisitionCost <= 0m)
            throw new ArgumentException("Acquisition cost must be positive.", nameof(acquisitionCost));

        List<PostLineRequest> lines = [];
        if (catchUp > 0m) lines.Add(new(accounts.DepreciationExpenseAccountId, "Debit", catchUp));
        if (currentAccumulated > 0m) lines.Add(new(accounts.AccumulatedDepreciationAccountId, "Debit", currentAccumulated));
        if (proceeds > 0m) lines.Add(new(accounts.DisposalProceedsAccountId, "Debit", proceeds));
        lines.Add(new(accounts.AssetCostAccountId, "Credit", acquisitionCost));
        if (gainLoss > 0m) lines.Add(new(accounts.GainOnDisposalAccountId, "Credit", gainLoss));
        else if (gainLoss < 0m) lines.Add(new(accounts.LossOnDisposalAccountId, "Debit", -gainLoss));

        return new PostEntryRequest(
            Id: EntryIdentity.ForSource(DisposalSourceType, disposalId),
            EffectiveDate: disposalDate,
            Reference: null,
            Memo: memo,
            Lines: lines,
            SourceRef: disposalId,
            SourceType: DisposalSourceType);
    }
}
