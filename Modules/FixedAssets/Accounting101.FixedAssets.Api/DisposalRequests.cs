using Accounting101.FixedAssets;

namespace Accounting101.FixedAssets.Api;

/// <summary>Dispose an asset. Proceeds of 0 is a retirement/scrap; amounts (catch-up, gain/loss) are
/// server-computed.</summary>
public sealed record DisposeAssetRequest(DateOnly DisposalDate, decimal Proceeds, string? Memo)
{
    public DisposeRequest ToRequest() => new(DisposalDate, Proceeds, Memo);
}
