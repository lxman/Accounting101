namespace Accounting101.FixedAssets;

/// <summary>The depreciation timeline math a disposal needs. Full-month convention: the in-service
/// month depreciates, the disposal month does not. Because every depreciation run applies exactly one
/// month via the same pure IDepreciationMethod, an asset's stored AccumulatedDepreciation after K runs
/// equals iterating the method K times from zero — so a disposal can compute the depreciation the asset
/// SHOULD have by its disposal date deterministically, without tracking how many runs were posted.</summary>
public static class DepreciationSchedule
{
    /// <summary>Whole months from the in-service month up to but EXCLUDING the disposal month; floored at 0.</summary>
    public static int MonthsBetween(DateOnly inService, DateOnly disposal)
    {
        int months = (disposal.Year * 12 + disposal.Month) - (inService.Year * 12 + inService.Month);
        return Math.Max(0, months);
    }

    /// <summary>The number of months to depreciate by the disposal date, capped at the asset's useful life.</summary>
    public static int TargetMonths(Asset asset, DateOnly disposal)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return Math.Min(MonthsBetween(asset.InServiceDate, disposal), asset.UsefulLifeMonths);
    }

    /// <summary>The accumulated depreciation after applying the method for <paramref name="months"/> whole
    /// months from zero. Stops early once a period yields zero (fully depreciated).</summary>
    public static decimal AccumulatedAfter(IDepreciationMethod method, Asset asset, int months)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(asset);
        decimal accumulated = 0m;
        for (int i = 0; i < months; i++)
        {
            Asset snapshot = asset with { AccumulatedDepreciation = accumulated };
            decimal step = method.DepreciationForPeriod(snapshot);
            if (step <= 0m) break;
            accumulated += step;
        }
        return accumulated;
    }
}
