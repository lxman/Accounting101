namespace Accounting101.FixedAssets;

/// <summary>Pure validation of an asset body. Returns null when valid, else a human-readable reason
/// (surfaced as 422 at the endpoint).</summary>
public static class AssetValidation
{
    public static string? Validate(AssetBody body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (string.IsNullOrWhiteSpace(body.Description))
            return "Description is required.";
        if (body.AcquisitionCost <= 0)
            return "AcquisitionCost must be greater than zero.";
        if (body.UsefulLifeMonths <= 0)
            return "UsefulLifeMonths must be greater than zero.";
        if (body.SalvageValue < 0)
            return "SalvageValue must not be negative.";
        if (body.SalvageValue > body.AcquisitionCost)
            return "SalvageValue must not exceed AcquisitionCost.";
        if (body.Method == DepreciationMethod.DecliningBalance)
        {
            if (body.DecliningBalanceFactor is not { } factor || factor <= 0)
                return "DecliningBalanceFactor must be greater than zero for the declining-balance method.";
        }
        else if (body.DecliningBalanceFactor is not null)
        {
            return "DecliningBalanceFactor is only valid for the declining-balance method.";
        }
        return null;
    }
}
