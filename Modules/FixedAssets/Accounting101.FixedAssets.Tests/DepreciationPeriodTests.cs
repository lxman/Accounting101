namespace Accounting101.FixedAssets.Tests;

/// <summary>Locks the boundary of <see cref="DepreciationPeriod.OnOrAfterServiceMonth"/> — the run-service
/// eligibility predicate — directly, independent of any run orchestration.</summary>
public sealed class DepreciationPeriodTests
{
    [Theory]
    [InlineData(2026, 2, false)]  // before the in-service month
    [InlineData(2026, 3, true)]   // the in-service month itself
    [InlineData(2027, 1, true)]   // a later year
    public void Boundary_around_a_March_in_service_date(int year, int month, bool expected)
    {
        DateOnly inService = new(2026, 3, 15);
        DepreciationPeriod period = new(year, month);
        Assert.Equal(expected, period.OnOrAfterServiceMonth(inService));
    }

    [Theory]
    [InlineData(2026, 5, false)]  // same year, earlier month
    [InlineData(2026, 6, true)]   // the in-service month itself
    [InlineData(2026, 12, true)]  // same year, later month
    public void Boundary_around_a_same_year_in_service_date(int year, int month, bool expected)
    {
        DateOnly inService = new(2026, 6, 1);
        DepreciationPeriod period = new(year, month);
        Assert.Equal(expected, period.OnOrAfterServiceMonth(inService));
    }
}
