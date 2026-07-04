namespace Accounting101.FixedAssets.Tests;

public sealed class DepreciationScheduleTests
{
    private static Asset Make(decimal cost, decimal salvage, int life, DepreciationMethod method,
        decimal? factor = null, DateOnly? inService = null) => new()
    {
        Id = Guid.NewGuid(),
        Description = "test",
        AcquisitionCost = cost,
        InServiceDate = inService ?? new DateOnly(2026, 1, 1),
        UsefulLifeMonths = life,
        SalvageValue = salvage,
        Method = method,
        DecliningBalanceFactor = factor,
        Status = AssetStatus.Active,
        AccumulatedDepreciation = 0m,
    };

    [Theory]
    [InlineData(2026, 1, 2026, 6, 5)]   // Jan in-service, Jun disposal → Jan..May = 5 months
    [InlineData(2026, 1, 2026, 1, 0)]   // same month → 0
    [InlineData(2026, 6, 2027, 1, 7)]   // Jun 2026 → Jan 2027 = Jun..Dec = 7 months
    [InlineData(2026, 6, 2026, 5, 0)]   // disposal before in-service → floored at 0
    public void MonthsBetween_counts_whole_months_excluding_disposal_month(
        int iy, int im, int dy, int dm, int expected)
    {
        Assert.Equal(expected, DepreciationSchedule.MonthsBetween(new DateOnly(iy, im, 15), new DateOnly(dy, dm, 3)));
    }

    [Fact]
    public void TargetMonths_caps_at_useful_life()
    {
        Asset a = Make(12000m, 0m, 24, DepreciationMethod.StraightLine, inService: new DateOnly(2026, 1, 1));
        // 60 whole months to disposal, but life is 24 → capped at 24.
        Assert.Equal(24, DepreciationSchedule.TargetMonths(a, new DateOnly(2031, 1, 1)));
    }

    [Fact]
    public void AccumulatedAfter_straight_line_equals_months_times_monthly()
    {
        // (12000-0)/24 = 500/mo; after 5 months = 2500.
        IDepreciationMethod sl = new StraightLineDepreciation();
        Asset a = Make(12000m, 0m, 24, DepreciationMethod.StraightLine);
        Assert.Equal(2500m, DepreciationSchedule.AccumulatedAfter(sl, a, 5));
    }

    [Fact]
    public void AccumulatedAfter_zero_months_is_zero()
    {
        IDepreciationMethod sl = new StraightLineDepreciation();
        Asset a = Make(12000m, 0m, 24, DepreciationMethod.StraightLine);
        Assert.Equal(0m, DepreciationSchedule.AccumulatedAfter(sl, a, 0));
    }

    [Fact]
    public void AccumulatedAfter_declining_balance_matches_iterated_single_periods()
    {
        // DB factor 2.0, life 24 → rate 1/12. Iterate 3 months by hand:
        // m1: 12000*0.083333=1000.00 -> 1000; m2: 11000*0.0833=916.67 -> 1916.67; m3: 10083.33*0.0833=840.28 -> 2756.95
        IDepreciationMethod db = new DecliningBalanceDepreciation();
        Asset a = Make(12000m, 0m, 24, DepreciationMethod.DecliningBalance, factor: 2.0m);
        decimal expected = 1000m + 916.67m + 840.28m;
        Assert.Equal(expected, DepreciationSchedule.AccumulatedAfter(db, a, 3));
    }

    [Fact]
    public void AccumulatedAfter_stops_at_the_method_floor()
    {
        // SL base 1000 over 3 months = 333.33/mo; asking for 10 months must not exceed 1000.
        IDepreciationMethod sl = new StraightLineDepreciation();
        Asset a = Make(1000m, 0m, 3, DepreciationMethod.StraightLine);
        Assert.Equal(1000m, DepreciationSchedule.AccumulatedAfter(sl, a, 10));
    }
}
