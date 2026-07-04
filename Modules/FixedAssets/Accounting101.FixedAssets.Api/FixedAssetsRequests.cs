using Accounting101.FixedAssets;

namespace Accounting101.FixedAssets.Api;

/// <summary>Create or update an asset. Status and AccumulatedDepreciation are server-owned and never sent.</summary>
public sealed record SaveAssetRequest(
    string Description,
    decimal AcquisitionCost,
    DateOnly InServiceDate,
    int UsefulLifeMonths,
    decimal SalvageValue,
    DepreciationMethod Method,
    decimal? DecliningBalanceFactor)
{
    public AssetBody ToBody() =>
        new(Description, AcquisitionCost, InServiceDate, UsefulLifeMonths, SalvageValue, Method, DecliningBalanceFactor);
}
