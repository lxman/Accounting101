namespace Accounting101.FixedAssets.Tests;

public sealed class AssetViewTests
{
    private static Asset Asset(AssetStatus status, decimal acquisitionCost, decimal accumulatedDepreciation) =>
        new()
        {
            Id = Guid.NewGuid(),
            Description = "Van",
            AcquisitionCost = acquisitionCost,
            InServiceDate = new DateOnly(2026, 1, 1),
            UsefulLifeMonths = 24,
            SalvageValue = 0m,
            Method = DepreciationMethod.StraightLine,
            DecliningBalanceFactor = null,
            Status = status,
            AccumulatedDepreciation = accumulatedDepreciation,
        };

    [Fact]
    public void Disposed_asset_has_zero_net_book_value_even_though_the_fold_cleared_to_zero()
    {
        // Post-disposal fold state: AccumulatedDepreciation resets to 0, so a naive
        // cost-minus-accumulated calculation would read the FULL acquisition cost.
        // A disposed asset is off the books — its current net book value is 0.
        Asset asset = Asset(AssetStatus.Disposed, 50_000m, 0m);

        var view = new AssetView(asset);

        Assert.Equal(0m, view.NetBookValue);
    }

    [Fact]
    public void Active_asset_net_book_value_is_cost_minus_accumulated_depreciation()
    {
        Asset asset = Asset(AssetStatus.Active, 50_000m, 12_000m);

        var view = new AssetView(asset);

        Assert.Equal(38_000m, view.NetBookValue);
    }
}
