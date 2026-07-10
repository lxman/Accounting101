namespace Accounting101.FixedAssets;

/// <summary>Read model for an asset — the register record plus its net book value (cost − accumulated
/// depreciation), a convenience for callers. A disposed asset is off the books: its current net book
/// value is 0, regardless of the accumulated-depreciation fold; the at-disposal figures (cost,
/// accumulated depreciation, gain/loss) live on the corresponding <c>Disposal</c> document.</summary>
public sealed record AssetView(Asset Asset)
{
    public decimal NetBookValue =>
        Asset.Status == AssetStatus.Disposed ? 0m : Asset.AcquisitionCost - Asset.AccumulatedDepreciation;
}
