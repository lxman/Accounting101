using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets.Tests;

public sealed class FixedAssetsRunServiceTests
{
    private static (FixedAssetsRunService svc, InMemoryAssetStore assets, InMemoryDepreciationRunStore runs,
        FakeLedgerClient ledger, FixedAccountsProvider accounts) Build()
    {
        InMemoryAssetStore assets = new();
        InMemoryDepreciationRunStore runs = new();
        FakeLedgerClient ledger = new();
        FixedAccountsProvider accounts = new();
        DepreciationMethodSelector selector = new([new StraightLineDepreciation(), new DecliningBalanceDepreciation()]);
        FixedAssetsRunService svc = new(assets, runs, selector, accounts, ledger);
        return (svc, assets, runs, ledger, accounts);
    }

    private static AssetBody Sl(decimal cost, int life, DateOnly inService) =>
        new("SL asset", cost, inService, life, 0m, DepreciationMethod.StraightLine, null);

    // Accumulated depreciation is no longer stored — read it from the ledger fold (negated contra-asset),
    // exactly as FixedAssetsService does.
    private static async Task<decimal> AccumAsync(
        FakeLedgerClient ledger, FixedAccountsProvider accounts, Guid clientId, Guid assetId) =>
        (await ledger.GetSubledgerAsync(clientId, accounts.AccumulatedDepreciationAccountId, "Asset", null, default, includePending: true))
            .Where(l => l.DimensionValue == assetId).Sum(l => -l.Balance);

    [Fact]
    public async Task Run_computes_lines_advances_assets_and_posts_one_aggregate_entry()
    {
        (FixedAssetsRunService svc, InMemoryAssetStore assets, _, FakeLedgerClient ledger, FixedAccountsProvider accounts) = Build();
        Guid clientId = Guid.NewGuid();
        Asset a = await assets.CreateAsync(clientId, Sl(12000m, 24, new DateOnly(2026, 1, 1)), default); // 500/mo
        Asset b = await assets.CreateAsync(clientId, Sl(6000m, 24, new DateOnly(2026, 1, 1)), default);  // 250/mo

        DepreciationRun run = await svc.RunDepreciationAsync(clientId, new DepreciationRunRequest(2026, 1, null, "Jan"), default);

        Assert.Equal(750m, run.Total);
        Assert.Equal(2, run.Lines.Count);
        Assert.Equal(500m, await AccumAsync(ledger, accounts, clientId, a.Id));
        Assert.Equal(250m, await AccumAsync(ledger, accounts, clientId, b.Id));

        PostEntryRequest posted = Assert.Single(ledger.Posted);
        Assert.Equal("DepreciationRun", posted.SourceType);
        Assert.Equal(750m, posted.Lines.Single(l => l.AccountId == accounts.DepreciationExpenseAccountId).Amount);
        Assert.Equal(new DateOnly(2026, 1, 31), posted.EffectiveDate); // default = last day of period
    }

    [Fact]
    public async Task Second_run_for_the_same_period_is_rejected()
    {
        (FixedAssetsRunService svc, InMemoryAssetStore assets, _, _, _) = Build();
        Guid clientId = Guid.NewGuid();
        await assets.CreateAsync(clientId, Sl(12000m, 24, new DateOnly(2026, 1, 1)), default);
        await svc.RunDepreciationAsync(clientId, new DepreciationRunRequest(2026, 1, null, null), default);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.RunDepreciationAsync(clientId, new DepreciationRunRequest(2026, 1, null, null), default));
    }

    [Fact]
    public async Task A_period_with_no_eligible_assets_throws_ArgumentException()
    {
        (FixedAssetsRunService svc, InMemoryAssetStore assets, _, _, _) = Build();
        Guid clientId = Guid.NewGuid();
        // Asset goes into service AFTER the run period → not eligible.
        await assets.CreateAsync(clientId, Sl(12000m, 24, new DateOnly(2026, 6, 1)), default);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.RunDepreciationAsync(clientId, new DepreciationRunRequest(2026, 1, null, null), default));
    }

    [Fact]
    public async Task Asset_in_its_in_service_month_earns_a_full_month()
    {
        (FixedAssetsRunService svc, InMemoryAssetStore assets, _, _, _) = Build();
        Guid clientId = Guid.NewGuid();
        Asset a = await assets.CreateAsync(clientId, Sl(12000m, 24, new DateOnly(2026, 1, 15)), default); // mid-month
        DepreciationRun run = await svc.RunDepreciationAsync(clientId, new DepreciationRunRequest(2026, 1, null, null), default);
        Assert.Equal(500m, run.Total); // full month despite Jan-15 in-service (full-month convention)
    }

    [Fact]
    public async Task Void_latest_run_reverses_entry_and_rolls_back_accumulated()
    {
        (FixedAssetsRunService svc, InMemoryAssetStore assets, _, FakeLedgerClient ledger, FixedAccountsProvider accounts) = Build();
        Guid clientId = Guid.NewGuid();
        Asset a = await assets.CreateAsync(clientId, Sl(12000m, 24, new DateOnly(2026, 1, 1)), default);
        DepreciationRun run = await svc.RunDepreciationAsync(clientId, new DepreciationRunRequest(2026, 1, null, null), default);
        Assert.Equal(500m, await AccumAsync(ledger, accounts, clientId, a.Id));

        DepreciationRun voided = await svc.VoidRunAsync(clientId, run.Id, "oops", default);
        Assert.Equal(DepreciationRunStatus.Voided, voided.Status);
        Assert.True(ledger.ReversedOrWithdrawn);
        Assert.Equal(0m, await AccumAsync(ledger, accounts, clientId, a.Id)); // entry reversal rolled the fold back
    }

    [Fact]
    public async Task Voiding_a_non_latest_run_is_rejected()
    {
        (FixedAssetsRunService svc, InMemoryAssetStore assets, _, _, _) = Build();
        Guid clientId = Guid.NewGuid();
        await assets.CreateAsync(clientId, Sl(12000m, 24, new DateOnly(2026, 1, 1)), default);
        DepreciationRun jan = await svc.RunDepreciationAsync(clientId, new DepreciationRunRequest(2026, 1, null, null), default);
        await svc.RunDepreciationAsync(clientId, new DepreciationRunRequest(2026, 2, null, null), default);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.VoidRunAsync(clientId, jan.Id, null, default)); // not latest
    }
}
