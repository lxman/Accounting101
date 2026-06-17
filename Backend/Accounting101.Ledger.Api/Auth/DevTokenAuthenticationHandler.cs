using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Accounting101.Ledger.Api.Auth;

/// <summary>
/// Authenticates a <see cref="DevToken"/> bearer (<c>Authorization: DevToken &lt;token&gt;</c>) into
/// a <see cref="ClaimsPrincipal"/>. A development stand-in for a real JWT/OIDC handler — nothing
/// downstream (authorization, the <see cref="IActorFactory"/>) knows or cares which scheme produced
/// the principal, so swapping in a production provider touches only the registration.
/// </summary>
public sealed class DevTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private const string Prefix = "DevToken ";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var header))
            return Task.FromResult(AuthenticateResult.NoResult());

        string value = header.ToString();
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(AuthenticateResult.NoResult());

        DevTokenPayload? payload = DevToken.TryDecode(value[Prefix.Length..].Trim());
        if (payload is null || payload.Sub == Guid.Empty)
            return Task.FromResult(AuthenticateResult.Fail("Malformed dev token."));

        List<Claim> claims = [new Claim(ClaimTypes.NameIdentifier, payload.Sub.ToString())];
        if (!string.IsNullOrEmpty(payload.Name))
            claims.Add(new Claim(ClaimTypes.Name, payload.Name));
        claims.AddRange((payload.Claims ?? []).Select(c => new Claim(c.Type, c.Value)));

        ClaimsPrincipal principal = new(new ClaimsIdentity(claims, Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }
}
