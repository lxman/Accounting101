namespace Accounting101.FixedAssets;

/// <summary>A calendar month a depreciation run targets. The run's default GL effective date is the
/// last day of this month.</summary>
public readonly record struct DepreciationPeriod(int Year, int Month)
{
    /// <summary>The last calendar day of the period (handles month length + leap years).</summary>
    public DateOnly LastDay() => new(Year, Month, DateTime.DaysInMonth(Year, Month));

    /// <summary>True when this period is on or after the asset's in-service month.</summary>
    public bool OnOrAfterServiceMonth(DateOnly inService) =>
        Year > inService.Year || (Year == inService.Year && Month >= inService.Month);
}
