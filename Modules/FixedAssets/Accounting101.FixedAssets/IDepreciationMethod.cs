namespace Accounting101.FixedAssets;

/// <summary>One depreciation method. Pure: given an asset's current stored state it returns the
/// depreciation for ONE period (full-month convention — every eligible period is one uniform month).
/// Never negative; never drives AccumulatedDepreciation past the method's floor.</summary>
public interface IDepreciationMethod
{
    DepreciationMethod Method { get; }

    /// <summary>Depreciation for one month given the asset's AcquisitionCost, SalvageValue,
    /// UsefulLifeMonths, AccumulatedDepreciation and (for declining balance) DecliningBalanceFactor.</summary>
    decimal DepreciationForPeriod(Asset asset);
}
