using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets.Tests;

public sealed class FixedAssetsRunServiceFa3Tests
{
    private static AssetBody Sl(decimal cost, int life, DateOnly inService) =>
        new("SL", cost, inService, life, 0m, DepreciationMethod.StraightLine, null);

    [Fact]
    public async Task Disposed_assets_are_excluded_from_a_depreciation_run()
    {
        InMemoryAssetStore assets = new();
        InMemoryDepreciationRunStore runs = new();
        FakeLedgerClient ledger = new();
        FixedAccountsProvider accounts = new();
        DepreciationMethodSelector selector = new([new StraightLineDepreciation(), new DecliningBalanceDepreciation()]);
        FixedAssetsRunService svc = new(assets, runs, selector, accounts, ledger);
        Guid clientId = Guid.NewGuid();

        Asset active = await assets.CreateAsync(clientId, Sl(12000m, 24, new DateOnly(2026, 1, 1)), default); // 500/mo
        Asset disposed = await assets.CreateAsync(clientId, Sl(6000m, 24, new DateOnly(2026, 1, 1)), default);
        await assets.MarkDisposedAsync(clientId, disposed.Id, default); // Disposed → excluded

        DepreciationRun run = await svc.RunDepreciationAsync(clientId, new DepreciationRunRequest(2026, 1, null, null), default);
        Assert.Equal(500m, run.Total);                 // only the active asset
        Assert.Equal(active.Id, Assert.Single(run.Lines).AssetId);
    }

    [Fact]
    public async Task A_run_that_cannot_resolve_accounts_persists_nothing_and_advances_nothing()
    {
        InMemoryAssetStore assets = new();
        InMemoryDepreciationRunStore runs = new();
        FakeLedgerClient ledger = new();
        ThrowingAccountsProvider accounts = new();
        DepreciationMethodSelector selector = new([new StraightLineDepreciation(), new DecliningBalanceDepreciation()]);
        FixedAssetsRunService svc = new(assets, runs, selector, accounts, ledger);
        Guid clientId = Guid.NewGuid();
        Asset a = await assets.CreateAsync(clientId, Sl(12000m, 24, new DateOnly(2026, 1, 1)), default);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.RunDepreciationAsync(clientId, new DepreciationRunRequest(2026, 1, null, null), default));

        // No run persisted, asset not advanced, nothing posted.
        Assert.Null(await runs.GetByPeriodAsync(clientId, new DepreciationPeriod(2026, 1), default));
        Assert.Equal(0m, (await assets.GetAsync(clientId, a.Id, default))!.AccumulatedDepreciation);
        Assert.Empty(ledger.Posted);
    }

    [Fact]
    public async Task Voiding_a_run_with_no_spawned_entry_still_marks_voided()
    {
        InMemoryAssetStore assets = new();
        InMemoryDepreciationRunStore runs = new();
        FakeLedgerClient ledger = new();
        FixedAccountsProvider accounts = new();
        DepreciationMethodSelector selector = new([new StraightLineDepreciation(), new DecliningBalanceDepreciation()]);
        FixedAssetsRunService svc = new(assets, runs, selector, accounts, ledger);
        Guid clientId = Guid.NewGuid();
        await assets.CreateAsync(clientId, Sl(12000m, 24, new DateOnly(2026, 1, 1)), default);

        DepreciationRun run = await svc.RunDepreciationAsync(clientId, new DepreciationRunRequest(2026, 1, null, null), default);

        // Simulate a run stranded by a post that never landed — the void can find no spawned entry to reverse.
        ledger.ReturnNoEntries = true;
        DepreciationRun voided = await svc.VoidRunAsync(clientId, run.Id, "recover", default);
        Assert.Equal(DepreciationRunStatus.Voided, voided.Status); // tolerated the missing entry, still recoverable
        Assert.False(ledger.ReversedOrWithdrawn);                  // nothing to reverse
    }
}

/// <summary>An accounts provider that always throws — to prove RunDepreciationAsync resolves accounts
/// before any persistence/mutation.</summary>
internal sealed class ThrowingAccountsProvider : IFixedAssetsAccountsProvider
{
    public Task<FixedAssetsPostingAccounts> GetAccountsAsync(Guid clientId, CancellationToken ct = default) =>
        throw new InvalidOperationException("Fixed-assets posting account is not configured.");
}
