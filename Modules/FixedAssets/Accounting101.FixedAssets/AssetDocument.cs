namespace Accounting101.FixedAssets;

/// <summary>The stored shape of an asset — the opaque reference-document body. The asset id is the
/// document id, so it is not repeated here. Status is server-owned; accumulated depreciation is no longer
/// stored — it is folded from the per-<c>{Asset}</c> ledger subledger on read.</summary>
public sealed record AssetDocument(
    string Description,
    decimal AcquisitionCost,
    DateOnly InServiceDate,
    int UsefulLifeMonths,
    decimal SalvageValue,
    DepreciationMethod Method,
    decimal? DecliningBalanceFactor,
    AssetStatus Status);
