using Accounting101.FixedAssets;

namespace Accounting101.FixedAssets.Tests;

public sealed class AssetValidationTests
{
    private static AssetBody Valid() => new(
        "Delivery van", 30000m, new DateOnly(2026, 1, 1), 60, 3000m, DepreciationMethod.StraightLine, null);

    [Fact]
    public void A_well_formed_straight_line_asset_is_valid() => Assert.Null(AssetValidation.Validate(Valid()));

    [Fact]
    public void A_well_formed_declining_balance_asset_is_valid()
    {
        AssetBody body = Valid() with { Method = DepreciationMethod.DecliningBalance, DecliningBalanceFactor = 2.0m };
        Assert.Null(AssetValidation.Validate(body));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_description_is_rejected(string description) =>
        Assert.NotNull(AssetValidation.Validate(Valid() with { Description = description }));

    [Fact]
    public void Non_positive_cost_is_rejected() =>
        Assert.NotNull(AssetValidation.Validate(Valid() with { AcquisitionCost = 0m }));

    [Fact]
    public void Non_positive_life_is_rejected() =>
        Assert.NotNull(AssetValidation.Validate(Valid() with { UsefulLifeMonths = 0 }));

    [Fact]
    public void Negative_salvage_is_rejected() =>
        Assert.NotNull(AssetValidation.Validate(Valid() with { SalvageValue = -1m }));

    [Fact]
    public void Salvage_above_cost_is_rejected() =>
        Assert.NotNull(AssetValidation.Validate(Valid() with { SalvageValue = 40000m }));

    [Fact]
    public void Declining_balance_without_a_positive_factor_is_rejected()
    {
        Assert.NotNull(AssetValidation.Validate(Valid() with { Method = DepreciationMethod.DecliningBalance, DecliningBalanceFactor = null }));
        Assert.NotNull(AssetValidation.Validate(Valid() with { Method = DepreciationMethod.DecliningBalance, DecliningBalanceFactor = 0m }));
    }

    [Fact]
    public void A_factor_on_a_straight_line_asset_is_rejected() =>
        Assert.NotNull(AssetValidation.Validate(Valid() with { DecliningBalanceFactor = 2.0m }));
}
