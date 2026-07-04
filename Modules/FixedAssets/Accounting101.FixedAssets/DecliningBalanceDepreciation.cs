namespace Accounting101.FixedAssets;

/// <summary>Declining-balance: net book value × (factor / life) each month, floored at salvage. The
/// period that would cross salvage takes exactly the remainder down to salvage; once at salvage it
/// returns 0. No straight-line crossover (FA-2 decision). DecliningBalanceFactor is guaranteed present
/// and positive by asset validation.</summary>
public sealed class DecliningBalanceDepreciation : IDepreciationMethod
{
    public DepreciationMethod Method => DepreciationMethod.DecliningBalance;

    public decimal DepreciationForPeriod(Asset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        decimal nbv = asset.AcquisitionCost - asset.AccumulatedDepreciation;
        decimal floorRemaining = nbv - asset.SalvageValue;
        if (floorRemaining <= 0m) return 0m;
        decimal factor = asset.DecliningBalanceFactor ?? 0m;
        decimal rate = factor / asset.UsefulLifeMonths;
        decimal raw = Math.Round(nbv * rate, 2, MidpointRounding.ToEven);
        return Math.Min(raw, floorRemaining);
    }
}
