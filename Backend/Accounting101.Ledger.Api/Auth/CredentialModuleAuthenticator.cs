using System.Security.Cryptography;
using System.Text;
using Accounting101.Ledger.Api.Control;
using Microsoft.AspNetCore.Http;

namespace Accounting101.Ledger.Api.Auth;

/// <summary>
/// Out-of-process credential verification: reads <c>X-Module-Key</c> + <c>X-Module-Secret</c>
/// from the current HTTP request, looks up the module registration, and compares the secret in
/// constant time via <see cref="CryptographicOperations.FixedTimeEquals"/>. Returns a
/// <see cref="ModuleIdentity"/> on a match, or null when the headers are absent, the key is
/// unknown, or the secret does not match.
///
/// Disabled modules: this authenticator authenticates them successfully — establishing identity
/// is independent of whether the module is permitted to act. The enabled/disabled gate is enforced
/// by <see cref="Control.ModuleAccess"/> (the gateway authorization layer, Task 3), not here.
///
/// Never logs the secret value.
/// </summary>
public sealed class CredentialModuleAuthenticator(
    IHttpContextAccessor httpContextAccessor,
    ControlStore controlStore) : IModuleAuthenticator
{
    public async Task<ModuleIdentity?> AuthenticateAsync()
    {
        HttpContext? ctx = httpContextAccessor.HttpContext;
        if (ctx is null)
            return null;

        string? key = ctx.Request.Headers["X-Module-Key"].FirstOrDefault();
        string? secret = ctx.Request.Headers["X-Module-Secret"].FirstOrDefault();

        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(secret))
            return null;

        ModuleRegistration? registration = await controlStore.GetModuleAsync(key);
        if (registration is null)
            return null;

        // Constant-time comparison to prevent timing attacks against the secret.
        // Compare UTF-8 byte spans so the comparison length is fixed per encoding.
        byte[] provided = Encoding.UTF8.GetBytes(secret);
        byte[] stored = Encoding.UTF8.GetBytes(registration.Secret);

        if (!CryptographicOperations.FixedTimeEquals(provided, stored))
            return null;

        return new ModuleIdentity(key);
    }
}
