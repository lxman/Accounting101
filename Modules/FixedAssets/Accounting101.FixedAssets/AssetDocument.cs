namespace Accounting101.FixedAssets;

/// <summary>The stored shape of an asset — the opaque reference-document body. The asset id is the
/// document id, so it is not repeated here. Status and AccumulatedDepreciation are server-owned.</summary>
public sealed record AssetDocument(
    string Description,
    decimal AcquisitionCost,
    DateOnly InServiceDate,
    int UsefulLifeMonths,
    decimal SalvageValue,
    DepreciationMethod Method,
    decimal? DecliningBalanceFactor,
    AssetStatus Status,
    decimal AccumulatedDepreciation);
