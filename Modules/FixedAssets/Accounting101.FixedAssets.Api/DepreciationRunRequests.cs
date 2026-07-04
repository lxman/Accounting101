using Accounting101.FixedAssets;

namespace Accounting101.FixedAssets.Api;

/// <summary>Run depreciation for a period. Amounts are server-computed; the caller supplies only the
/// period and optional overrides.</summary>
public sealed record RunDepreciationRequest(int Year, int Month, DateOnly? EffectiveDate, string? Memo)
{
    public DepreciationRunRequest ToRequest() => new(Year, Month, EffectiveDate, Memo);
}

/// <summary>Optional reason on a void.</summary>
public sealed record VoidReasonRequest(string? Reason);
