namespace Accounting101.FixedAssets;

/// <summary>One asset's depreciation within a run.</summary>
public sealed record DepreciationRunLine(Guid AssetId, decimal Amount);

/// <summary>The stored body of a depreciation run (the evidentiary document body).</summary>
public sealed record DepreciationRunBody(
    DepreciationPeriod Period,
    DateOnly EffectiveDate,
    string? Memo,
    IReadOnlyList<DepreciationRunLine> Lines,
    decimal Total);

/// <summary>Lifecycle of a run: posted, or voided (LIFO).</summary>
public enum DepreciationRunStatus
{
    Posted = 0,
    Voided = 1,
}

/// <summary>A depreciation run — the engine assigns the number; status is derived from the document
/// lifecycle.</summary>
public sealed record DepreciationRun
{
    public required Guid Id { get; init; }
    public string? Number { get; init; }
    public required DepreciationPeriod Period { get; init; }
    public required DateOnly EffectiveDate { get; init; }
    public string? Memo { get; init; }
    public required IReadOnlyList<DepreciationRunLine> Lines { get; init; }
    public required decimal Total { get; init; }
    public required DepreciationRunStatus Status { get; init; }
}

/// <summary>Read model for a depreciation run.</summary>
public sealed record DepreciationRunView(DepreciationRun Run);
