namespace Accounting101.FixedAssets;

/// <summary>Read model for an asset — the register record plus its net book value (cost − accumulated
/// depreciation), a convenience for callers.</summary>
public sealed record AssetView(Asset Asset)
{
    public decimal NetBookValue => Asset.AcquisitionCost - Asset.AccumulatedDepreciation;
}
