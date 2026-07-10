using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets.Tests;

public sealed class AccumulatedDepreciationFoldTests
{
    [Fact]
    public async Task Reported_asset_accumulated_depreciation_is_the_negated_ledger_fold()
    {
        FaHarness h = FaHarness.Build();
        Guid client = Guid.NewGuid();
        Asset asset = await h.Assets.CreateAsync(client, StraightLine(cost: 12_000m, life: 12, salvage: 0m, new DateOnly(2026, 1, 1)));

        // One month depreciation run posts a {Asset} credit of 1000 to Accumulated Depreciation.
        await h.RunService.RunDepreciationAsync(client, new DepreciationRunRequest(2026, 1, null, null));

        // The service reports accumulated depreciation folded from the ledger (negated contra-asset), = 1000.
        Asset? read = await h.AssetService.GetAsync(client, asset.Id);
        Assert.Equal(1_000m, read!.AccumulatedDepreciation);
    }

    private static AssetBody StraightLine(decimal cost, int life, decimal salvage, DateOnly inService) =>
        new("SL asset", cost, inService, life, salvage, DepreciationMethod.StraightLine, null);
}

/// <summary>Wires the FA services against the shared in-memory fakes so a test can drive a run and then
/// read an asset back through the report path (the ledger fold), the same objects the DI graph composes.</summary>
internal sealed class FaHarness
{
    public required InMemoryAssetStore Assets { get; init; }
    public required FixedAssetsService AssetService { get; init; }
    public required FixedAssetsRunService RunService { get; init; }
    public required FakeLedgerClient Ledger { get; init; }
    public required FixedAccountsProvider Accounts { get; init; }

    public static FaHarness Build()
    {
        InMemoryAssetStore assets = new();
        InMemoryDepreciationRunStore runs = new();
        FakeLedgerClient ledger = new();
        FixedAccountsProvider accounts = new();
        DepreciationMethodSelector selector = new([new StraightLineDepreciation(), new DecliningBalanceDepreciation()]);
        return new FaHarness
        {
            Assets = assets,
            AssetService = new FixedAssetsService(assets, ledger, accounts),
            RunService = new FixedAssetsRunService(assets, runs, selector, accounts, ledger),
            Ledger = ledger,
            Accounts = accounts,
        };
    }
}
