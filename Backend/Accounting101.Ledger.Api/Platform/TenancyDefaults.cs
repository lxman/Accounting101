using Microsoft.Extensions.Configuration;

namespace Accounting101.Ledger.Api.Platform;

/// <summary>
/// Single-firm defaults. On-site and any request without a <see cref="FirmClaims.FirmId"/> claim resolve
/// to <see cref="DefaultFirmId"/> (overridable via <c>Tenancy:DefaultFirmId</c>). The constant is a stable
/// well-known id so a deployment's single firm has the same handle across restarts.
/// </summary>
public static class TenancyDefaults
{
    public static readonly Guid DefaultFirmId = new("f1f10000-0000-0000-0000-000000000001");

    public static Guid ResolveDefaultFirmId(IConfiguration configuration) =>
        Guid.TryParse(configuration["Tenancy:DefaultFirmId"], out Guid id) ? id : DefaultFirmId;

    /// <summary>Whether the platform-operator control plane (/platform/*) is exposed. OFF by default — an
    /// on-site single-firm deployment has no operator tier. A SaaS operator sets Tenancy:Platform:Enabled=true.</summary>
    public static bool PlatformEnabled(IConfiguration configuration) =>
        bool.TryParse(configuration["Tenancy:Platform:Enabled"], out bool enabled) && enabled;
}
