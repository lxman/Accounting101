namespace Accounting101.FixedAssets;

/// <summary>The stored body of a disposal (the evidentiary document body). The breakdown is retained for
/// audit and to roll the asset back on void (AccumulatedBeforeDisposal).</summary>
public sealed record DisposalBody(
    Guid AssetId,
    DateOnly DisposalDate,
    decimal Proceeds,
    decimal CatchUpDepreciation,
    decimal AccumulatedBeforeDisposal,
    decimal AccumulatedAtDisposal,
    decimal NetBookValue,
    decimal GainLoss,
    string? Memo);

/// <summary>Lifecycle of a disposal: posted, or voided.</summary>
public enum DisposalStatus
{
    Posted = 0,
    Voided = 1,
}

/// <summary>A disposal — the engine assigns the number; status is derived from the document lifecycle.</summary>
public sealed record Disposal
{
    public required Guid Id { get; init; }
    public string? Number { get; init; }
    public required Guid AssetId { get; init; }
    public required DateOnly DisposalDate { get; init; }
    public required decimal Proceeds { get; init; }
    public required decimal CatchUpDepreciation { get; init; }
    public required decimal AccumulatedBeforeDisposal { get; init; }
    public required decimal AccumulatedAtDisposal { get; init; }
    public required decimal NetBookValue { get; init; }
    public required decimal GainLoss { get; init; }
    public string? Memo { get; init; }
    public required DisposalStatus Status { get; init; }
}

/// <summary>Read model for a disposal.</summary>
public sealed record DisposalView(Disposal Disposal);
