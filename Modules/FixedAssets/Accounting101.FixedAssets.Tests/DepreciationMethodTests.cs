namespace Accounting101.FixedAssets.Tests;

public sealed class DepreciationMethodTests
{
    private static Asset Make(decimal cost, decimal salvage, int lifeMonths, decimal accumulated,
        DepreciationMethod method, decimal? factor = null) => new()
    {
        Id = Guid.NewGuid(),
        Description = "test",
        AcquisitionCost = cost,
        InServiceDate = new DateOnly(2026, 1, 1),
        UsefulLifeMonths = lifeMonths,
        SalvageValue = salvage,
        Method = method,
        DecliningBalanceFactor = factor,
        Status = AssetStatus.Active,
        AccumulatedDepreciation = accumulated,
    };

    // ── Straight line ────────────────────────────────────────────────────────

    [Fact]
    public void StraightLine_uniform_monthly_amount()
    {
        // (12000 - 0) / 24 = 500/mo
        IDepreciationMethod sut = new StraightLineDepreciation();
        Assert.Equal(DepreciationMethod.StraightLine, sut.Method);
        Asset a = Make(12000m, 0m, 24, accumulated: 0m, DepreciationMethod.StraightLine);
        Assert.Equal(500m, sut.DepreciationForPeriod(a));
    }

    [Fact]
    public void StraightLine_honors_salvage_in_the_base()
    {
        // (12000 - 2400) / 24 = 400/mo
        IDepreciationMethod sut = new StraightLineDepreciation();
        Asset a = Make(12000m, 2400m, 24, accumulated: 0m, DepreciationMethod.StraightLine);
        Assert.Equal(400m, sut.DepreciationForPeriod(a));
    }

    [Fact]
    public void StraightLine_final_period_takes_the_exact_remainder()
    {
        // base 1000, monthly ~ 333.33; after 999.99 accumulated, remainder is 0.01
        IDepreciationMethod sut = new StraightLineDepreciation();
        Asset a = Make(1000m, 0m, 3, accumulated: 999.99m, DepreciationMethod.StraightLine);
        Assert.Equal(0.01m, sut.DepreciationForPeriod(a));
    }

    [Fact]
    public void StraightLine_returns_zero_when_fully_depreciated()
    {
        IDepreciationMethod sut = new StraightLineDepreciation();
        Asset a = Make(12000m, 2400m, 24, accumulated: 9600m, DepreciationMethod.StraightLine);
        Assert.Equal(0m, sut.DepreciationForPeriod(a));
    }

    [Fact]
    public void StraightLine_rounds_to_cents()
    {
        // 1000 / 3 = 333.333... -> 333.33
        IDepreciationMethod sut = new StraightLineDepreciation();
        Asset a = Make(1000m, 0m, 3, accumulated: 0m, DepreciationMethod.StraightLine);
        Assert.Equal(333.33m, sut.DepreciationForPeriod(a));
    }

    // ── Declining balance ────────────────────────────────────────────────────

    [Fact]
    public void DecliningBalance_first_period_is_nbv_times_rate()
    {
        // rate = 2.0/24 = 0.08333...; nbv 12000; 12000 * 0.083333 = 1000.00
        IDepreciationMethod sut = new DecliningBalanceDepreciation();
        Assert.Equal(DepreciationMethod.DecliningBalance, sut.Method);
        Asset a = Make(12000m, 0m, 24, accumulated: 0m, DepreciationMethod.DecliningBalance, factor: 2.0m);
        Assert.Equal(1000m, sut.DepreciationForPeriod(a));
    }

    [Fact]
    public void DecliningBalance_second_period_uses_reduced_book_value()
    {
        // after 1000 accumulated, nbv 11000; 11000 * 0.083333 = 916.67
        IDepreciationMethod sut = new DecliningBalanceDepreciation();
        Asset a = Make(12000m, 0m, 24, accumulated: 1000m, DepreciationMethod.DecliningBalance, factor: 2.0m);
        Assert.Equal(916.67m, sut.DepreciationForPeriod(a));
    }

    [Fact]
    public void DecliningBalance_never_depreciates_below_salvage()
    {
        // nbv 1100, salvage 1000 -> floor remaining is 100; raw would be 1100*0.0833=91.67 < 100, so 91.67
        IDepreciationMethod sut = new DecliningBalanceDepreciation();
        Asset a = Make(12000m, 1000m, 24, accumulated: 10900m, DepreciationMethod.DecliningBalance, factor: 2.0m);
        Assert.Equal(91.67m, sut.DepreciationForPeriod(a));
    }

    [Fact]
    public void DecliningBalance_crossing_period_takes_exact_remainder_to_salvage()
    {
        // nbv 1050, salvage 1000 -> floor remaining 50; raw 1050*0.0833=87.5 > 50, so take 50
        IDepreciationMethod sut = new DecliningBalanceDepreciation();
        Asset a = Make(12000m, 1000m, 24, accumulated: 10950m, DepreciationMethod.DecliningBalance, factor: 2.0m);
        Assert.Equal(50m, sut.DepreciationForPeriod(a));
    }

    [Fact]
    public void DecliningBalance_returns_zero_once_at_salvage()
    {
        IDepreciationMethod sut = new DecliningBalanceDepreciation();
        Asset a = Make(12000m, 1000m, 24, accumulated: 11000m, DepreciationMethod.DecliningBalance, factor: 2.0m);
        Assert.Equal(0m, sut.DepreciationForPeriod(a));
    }

    // ── Selector ─────────────────────────────────────────────────────────────

    [Fact]
    public void Selector_returns_the_matching_strategy()
    {
        DepreciationMethodSelector selector = new(
            [new StraightLineDepreciation(), new DecliningBalanceDepreciation()]);
        Assert.IsType<StraightLineDepreciation>(selector.For(DepreciationMethod.StraightLine));
        Assert.IsType<DecliningBalanceDepreciation>(selector.For(DepreciationMethod.DecliningBalance));
    }
}
