namespace Accounting101.FixedAssets;

/// <summary>The editable parameters of an asset (create/update input). Status and AccumulatedDepreciation
/// are server-owned and are NOT part of the body.</summary>
public sealed record AssetBody(
    string Description,
    decimal AcquisitionCost,
    DateOnly InServiceDate,
    int UsefulLifeMonths,
    decimal SalvageValue,
    DepreciationMethod Method,
    decimal? DecliningBalanceFactor);
