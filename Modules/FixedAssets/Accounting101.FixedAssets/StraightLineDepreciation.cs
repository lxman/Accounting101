namespace Accounting101.FixedAssets;

/// <summary>Straight-line: an equal share of the depreciable base (cost − salvage) each month over the
/// useful life. The final period takes the exact remainder so accumulated never exceeds the base.</summary>
public sealed class StraightLineDepreciation : IDepreciationMethod
{
    public DepreciationMethod Method => DepreciationMethod.StraightLine;

    public decimal DepreciationForPeriod(Asset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        decimal depreciableBase = asset.AcquisitionCost - asset.SalvageValue;
        decimal remaining = depreciableBase - asset.AccumulatedDepreciation;
        if (remaining <= 0m) return 0m;
        decimal monthly = Math.Round(depreciableBase / asset.UsefulLifeMonths, 2, MidpointRounding.ToEven);
        return Math.Min(monthly, remaining);
    }
}
