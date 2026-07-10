namespace Accounting101.FixedAssets;

/// <summary>A fixed asset in the register. Its Id is the reference-document id.</summary>
public sealed record Asset
{
    public required Guid Id { get; init; }
    public required string Description { get; init; }
    public required decimal AcquisitionCost { get; init; }
    public required DateOnly InServiceDate { get; init; }
    public required int UsefulLifeMonths { get; init; }
    public required decimal SalvageValue { get; init; }
    public required DepreciationMethod Method { get; init; }
    public decimal? DecliningBalanceFactor { get; init; }
    public required AssetStatus Status { get; init; }
    public decimal AccumulatedDepreciation { get; init; }
}
