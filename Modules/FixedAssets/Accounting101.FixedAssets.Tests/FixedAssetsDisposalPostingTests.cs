using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets.Tests;

public sealed class FixedAssetsDisposalPostingTests
{
    private static decimal Signed(PostLineRequest l) => l.Direction == "Debit" ? l.Amount : -l.Amount;

    private static FixedAssetsPostingAccounts Accounts() => new()
    {
        DepreciationExpenseAccountId = Guid.NewGuid(),
        AccumulatedDepreciationAccountId = Guid.NewGuid(),
        AssetCostAccountId = Guid.NewGuid(),
        DisposalProceedsAccountId = Guid.NewGuid(),
        GainOnDisposalAccountId = Guid.NewGuid(),
        LossOnDisposalAccountId = Guid.NewGuid(),
    };

    [Fact]
    public void Sale_at_a_gain_produces_a_balanced_entry_with_a_gain_credit()
    {
        FixedAssetsPostingAccounts a = Accounts();
        // cost 12000, currentAccum 5000, catchUp 500 -> finalAccum 5500, NBV 6500; proceeds 8000 -> gain 1500.
        PostEntryRequest e = FixedAssetsDisposalPosting.ComposeDisposal(
            Guid.NewGuid(), new DateOnly(2026, 6, 30), 12000m, 5000m, 500m, 8000m, 1500m, "sold", a);

        Assert.Equal(0m, e.Lines.Sum(Signed)); // balanced
        Assert.Equal(500m, e.Lines.Single(l => l.AccountId == a.DepreciationExpenseAccountId && l.Direction == "Debit").Amount);
        Assert.Equal(5000m, e.Lines.Single(l => l.AccountId == a.AccumulatedDepreciationAccountId && l.Direction == "Debit").Amount);
        Assert.Equal(8000m, e.Lines.Single(l => l.AccountId == a.DisposalProceedsAccountId && l.Direction == "Debit").Amount);
        Assert.Equal(12000m, e.Lines.Single(l => l.AccountId == a.AssetCostAccountId && l.Direction == "Credit").Amount);
        Assert.Equal(1500m, e.Lines.Single(l => l.AccountId == a.GainOnDisposalAccountId && l.Direction == "Credit").Amount);
        Assert.DoesNotContain(e.Lines, l => l.AccountId == a.LossOnDisposalAccountId);
    }

    [Fact]
    public void Sale_at_a_loss_produces_a_balanced_entry_with_a_loss_debit()
    {
        FixedAssetsPostingAccounts a = Accounts();
        // cost 12000, currentAccum 3000, catchUp 0 -> finalAccum 3000, NBV 9000; proceeds 7000 -> loss 2000.
        PostEntryRequest e = FixedAssetsDisposalPosting.ComposeDisposal(
            Guid.NewGuid(), new DateOnly(2026, 6, 30), 12000m, 3000m, 0m, 7000m, -2000m, null, a);

        Assert.Equal(0m, e.Lines.Sum(Signed));
        Assert.DoesNotContain(e.Lines, l => l.AccountId == a.DepreciationExpenseAccountId); // no catch-up line
        Assert.Equal(2000m, e.Lines.Single(l => l.AccountId == a.LossOnDisposalAccountId && l.Direction == "Debit").Amount);
        Assert.DoesNotContain(e.Lines, l => l.AccountId == a.GainOnDisposalAccountId);
    }

    [Fact]
    public void Retirement_with_zero_proceeds_omits_the_cash_line_and_balances()
    {
        FixedAssetsPostingAccounts a = Accounts();
        // cost 12000, currentAccum 12000, catchUp 0 -> NBV 0; proceeds 0 -> gain/loss 0.
        PostEntryRequest e = FixedAssetsDisposalPosting.ComposeDisposal(
            Guid.NewGuid(), new DateOnly(2026, 6, 30), 12000m, 12000m, 0m, 0m, 0m, "scrapped", a);

        Assert.Equal(0m, e.Lines.Sum(Signed));
        Assert.DoesNotContain(e.Lines, l => l.AccountId == a.DisposalProceedsAccountId); // no cash line
        Assert.DoesNotContain(e.Lines, l => l.AccountId == a.GainOnDisposalAccountId);
        Assert.DoesNotContain(e.Lines, l => l.AccountId == a.LossOnDisposalAccountId);
        // Dr Accum 12000 / Cr AssetCost 12000
        Assert.Equal(2, e.Lines.Count);
    }

    [Fact]
    public void Carries_source_type_and_deterministic_id()
    {
        FixedAssetsPostingAccounts a = Accounts();
        Guid id = Guid.NewGuid();
        PostEntryRequest x = FixedAssetsDisposalPosting.ComposeDisposal(id, new DateOnly(2026, 6, 30), 12000m, 5000m, 0m, 8000m, 1000m, null, a);
        PostEntryRequest y = FixedAssetsDisposalPosting.ComposeDisposal(id, new DateOnly(2026, 6, 30), 12000m, 5000m, 0m, 8000m, 1000m, null, a);
        Assert.Equal("Disposal", x.SourceType);
        Assert.Equal(id, x.SourceRef);
        Assert.Equal(EntryIdentity.ForSource(FixedAssetsDisposalPosting.DisposalSourceType, id), x.Id);
        Assert.Equal(x.Id, y.Id);
    }
}
