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
    public void Depreciation_run_posts_aggregate_expense_debit_and_per_asset_accum_credits()
    {
        var accounts = MakeAccounts(out Guid expense, out Guid accumulated);
        Guid a1 = Guid.NewGuid(), a2 = Guid.NewGuid();
        var lines = new List<DepreciationRunLine> { new(a1, 200m), new(a2, 100m) };

        PostEntryRequest entry = FixedAssetsPosting.ComposeDepreciationRun(
            Guid.NewGuid(), lines, 300m, new DateOnly(2026, 6, 30), null, accounts);

        Assert.Equal(3, entry.Lines.Count); // 1 expense debit + 2 asset credits
        PostLineRequest expenseLine = Assert.Single(entry.Lines, l => l.AccountId == expense);
        Assert.Equal("Debit", expenseLine.Direction);
        Assert.Equal(300m, expenseLine.Amount);
        Assert.Null(expenseLine.Dimensions); // expense is aggregate, not per-asset

        PostLineRequest c1 = Assert.Single(entry.Lines, l => l.AccountId == accumulated && l.Dimensions!["Asset"] == a1);
        Assert.Equal("Credit", c1.Direction);
        Assert.Equal(200m, c1.Amount);
        PostLineRequest c2 = Assert.Single(entry.Lines, l => l.AccountId == accumulated && l.Dimensions!["Asset"] == a2);
        Assert.Equal(100m, c2.Amount);

        Assert.Equal(0m, entry.Lines.Sum(Signed)); // balanced
        Assert.Equal(new DateOnly(2026, 6, 30), entry.EffectiveDate);
    }

    [Fact]
    public void Compose_carries_source_type_and_deterministic_id()
    {
        Guid runId = Guid.NewGuid();
        FixedAssetsPostingAccounts accounts = MakeAccounts(out _, out _);
        var lines = new List<DepreciationRunLine> { new(Guid.NewGuid(), 500m) };

        PostEntryRequest a = FixedAssetsPosting.ComposeDepreciationRun(runId, lines, 500m, new DateOnly(2026, 1, 31), null, accounts);
        PostEntryRequest b = FixedAssetsPosting.ComposeDepreciationRun(runId, lines, 500m, new DateOnly(2026, 1, 31), null, accounts);

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
        var lines = new List<DepreciationRunLine> { new(Guid.NewGuid(), 500m) };
        Assert.Throws<ArgumentException>(() =>
            FixedAssetsPosting.ComposeDepreciationRun(Guid.NewGuid(), lines, total, new DateOnly(2026, 1, 31), null, accounts));
    }

    [Fact]
    public void Period_last_day_handles_february()
    {
        Assert.Equal(new DateOnly(2026, 2, 28), new DepreciationPeriod(2026, 2).LastDay());
        Assert.Equal(new DateOnly(2028, 2, 29), new DepreciationPeriod(2028, 2).LastDay()); // leap
    }
}
