using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets.Tests;

public sealed class FixedAssetsPostingTests
{
    private static decimal Signed(PostLineRequest l) => l.Direction == "Debit" ? l.Amount : -l.Amount;

    private static FixedAssetsPostingAccounts MakeAccounts(out Guid expense, out Guid accumulated)
    {
        expense = Guid.NewGuid();
        accumulated = Guid.NewGuid();
        return new FixedAssetsPostingAccounts
        {
            DepreciationExpenseAccountId = expense,
            AccumulatedDepreciationAccountId = accumulated,
            AssetCostAccountId = Guid.NewGuid(),
            DisposalProceedsAccountId = Guid.NewGuid(),
            GainOnDisposalAccountId = Guid.NewGuid(),
            LossOnDisposalAccountId = Guid.NewGuid(),
        };
    }

    [Fact]
    public void Compose_debits_expense_and_credits_accumulated_balanced()
    {
        Guid runId = Guid.NewGuid();
        FixedAssetsPostingAccounts accounts = MakeAccounts(out Guid expense, out Guid accumulated);

        PostEntryRequest entry = FixedAssetsPosting.ComposeDepreciationRun(
            runId, total: 1416.67m, effectiveDate: new DateOnly(2026, 1, 31), memo: "Jan depreciation", accounts);

        Assert.Equal(2, entry.Lines.Count);
        PostLineRequest expLine = entry.Lines.Single(l => l.AccountId == expense);
        Assert.Equal("Debit", expLine.Direction);
        Assert.Equal(1416.67m, expLine.Amount);
        PostLineRequest accLine = entry.Lines.Single(l => l.AccountId == accumulated);
        Assert.Equal("Credit", accLine.Direction);
        Assert.Equal(1416.67m, accLine.Amount);
        Assert.Equal(0m, entry.Lines.Sum(Signed)); // balanced
        Assert.Equal(new DateOnly(2026, 1, 31), entry.EffectiveDate);
        Assert.Equal("Jan depreciation", entry.Memo);
    }

    [Fact]
    public void Compose_carries_source_type_and_deterministic_id()
    {
        Guid runId = Guid.NewGuid();
        FixedAssetsPostingAccounts accounts = MakeAccounts(out _, out _);

        PostEntryRequest a = FixedAssetsPosting.ComposeDepreciationRun(runId, 500m, new DateOnly(2026, 1, 31), null, accounts);
        PostEntryRequest b = FixedAssetsPosting.ComposeDepreciationRun(runId, 500m, new DateOnly(2026, 1, 31), null, accounts);

        Assert.Equal("DepreciationRun", a.SourceType);
        Assert.Equal(runId, a.SourceRef);
        Assert.Equal(EntryIdentity.ForSource(FixedAssetsPosting.DepreciationRunSourceType, runId), a.Id);
        Assert.Equal(a.Id, b.Id); // deterministic
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Compose_throws_when_total_not_positive(int total)
    {
        FixedAssetsPostingAccounts accounts = MakeAccounts(out _, out _);
        Assert.Throws<ArgumentException>(() =>
            FixedAssetsPosting.ComposeDepreciationRun(Guid.NewGuid(), total, new DateOnly(2026, 1, 31), null, accounts));
    }

    [Fact]
    public void Period_last_day_handles_february()
    {
        Assert.Equal(new DateOnly(2026, 2, 28), new DepreciationPeriod(2026, 2).LastDay());
        Assert.Equal(new DateOnly(2028, 2, 29), new DepreciationPeriod(2028, 2).LastDay()); // leap
    }
}
