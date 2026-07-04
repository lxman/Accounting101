using System.Security.Cryptography;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Api.Platform;
using Microsoft.Extensions.Hosting;

namespace Accounting101.Ledger.Api.Hosting;

/// <summary>
/// On startup, resolves each installed module's shared secret from <c>platform_control</c> (generating +
/// persisting one on first use) and populates the in-process <see cref="ModuleCredential"/> (what the
/// module sends) and the <see cref="ModuleRegistration"/> singleton (what <see cref="ModuleRegistrar"/> and
/// firm provisioning write into control DBs). Registered before <see cref="ModuleRegistrar"/> so the
/// registrations it seeds carry the persisted secret. Because the secret is persisted once and loaded
/// thereafter, it is stable across restarts and identical across instances — so a module authenticates
/// against any firm's control DB regardless of which process seeded it. The secret is never logged.
/// <para>
/// A credential's secret is empty until <see cref="StartAsync"/> populates it. Hosted services complete
/// before the server (itself a hosted service) begins accepting requests, so this is normally invisible;
/// should a module call somehow race host startup, the empty secret fails authentication CLOSED (a
/// spurious 403, never a bypass) — the safe default.
/// </para>
/// </summary>
public sealed class ModuleSecretResolver(
    IEnumerable<ModuleRegistration> registrations,
    IEnumerable<ModuleCredential> credentials,
    PlatformStore platform) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, ModuleCredential> credentialByKey = credentials.ToDictionary(c => c.Key);
        foreach (ModuleRegistration registration in registrations)
        {
            string secret = await platform.GetOrCreateModuleSecretAsync(registration.Key, GenerateSecret, cancellationToken);
            registration.Secret = secret;
            if (credentialByKey.TryGetValue(registration.Key, out ModuleCredential? credential))
                credential.Secret = secret;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // The only place a module secret is minted: 32 cryptographically random bytes → Base64URL (no padding).
    private static string GenerateSecret() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
