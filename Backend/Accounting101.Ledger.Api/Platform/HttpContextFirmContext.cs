using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Accounting101.Ledger.Api.Platform;

/// <summary>
/// Reads the <see cref="FirmClaims.FirmId"/> claim off the current request's principal; when it is absent
/// or unparsable (a single-firm/on-site token), falls back to the configured default firm. IdP-agnostic —
/// a production JWT issuer just needs to emit the "firm" claim.
/// </summary>
public sealed class HttpContextFirmContext(IHttpContextAccessor accessor, IConfiguration configuration) : IFirmContext
{
    public Guid FirmId
    {
        get
        {
            string? claim = accessor.HttpContext?.User.FindFirstValue(FirmClaims.FirmId);
            return Guid.TryParse(claim, out Guid id) ? id : TenancyDefaults.ResolveDefaultFirmId(configuration);
        }
    }
}
