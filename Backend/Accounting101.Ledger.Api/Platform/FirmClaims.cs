namespace Accounting101.Ledger.Api.Platform;

/// <summary>Claim types carried by an authenticated principal for tenancy.</summary>
public static class FirmClaims
{
    /// <summary>The firm the request acts within (a GUID). Absent on legacy/single-firm tokens, which
    /// fall back to the configured default firm.</summary>
    public const string FirmId = "firm";
}
