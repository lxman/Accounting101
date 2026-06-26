using System.Security.Cryptography;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Api.Documents;
using Accounting101.Ledger.Api.Tenancy;
using Accounting101.Ledger.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Accounting101.Ledger.Api.Hosting;

/// <summary>
/// Host-side wiring a module calls to install itself: stamps its in-process identity and contributes
/// a control-DB registration. "Installed module" = its <c>AddModule</c> line is present in the host's
/// composition root. The engine does not depend on modules; this is the seam they call into.
/// </summary>
public static class ModuleHostingExtensions
{
    public static IServiceCollection AddModule(this IServiceCollection services, ModuleIdentity identity, string name)
    {
        ArgumentNullException.ThrowIfNull(identity);

        // Generate a cryptographically random per-module secret: 32 bytes → Base64URL (no padding).
        // This value rides in the control DB; the in-process copy is the ModuleCredential below.
        string secret = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        ModuleCredential credential = new(identity.Key, secret);

        services.AddSingleton(identity);

        // In-process path (document store): keyed so it coexists with the credential authenticator.
        // Resolved by key ("host-stamped") wherever the in-process identity is required directly;
        // does not interfere with the default IModuleAuthenticator resolution below.
        services.AddKeyedSingleton<IModuleAuthenticator>(
            "host-stamped",
            (_, _) => new HostStampedModuleAuthenticator(identity));

        // HTTP posting path: the credential-verifying authenticator is the default IModuleAuthenticator.
        // Scoped so it can access the per-request IHttpContextAccessor. TryAdd so the first registered
        // module's credential authenticator wins — all modules share the same request-pipeline authenticator
        // since it looks the module up by key from the request header, not from DI registration order.
        services.TryAddScoped<IModuleAuthenticator, CredentialModuleAuthenticator>();

        // The module's in-process credential: injected into HttpLedgerClient (Task 4) so it can
        // attach X-Module-Key / X-Module-Secret headers when posting to the engine over HTTP.
        services.AddSingleton(credential);

        services.AddSingleton(new ModuleRegistration
        {
            Key = identity.Key,
            Name = name,
            Enabled = true,
            Secret = secret,
        });

        // One registrar regardless of how many modules install themselves — it upserts every
        // contributed ModuleRegistration on startup. TryAddEnumerable dedupes by implementation type.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ModuleRegistrar>());

        return services;
    }

    /// <summary>
    /// Install a module that uses the document store: stamps its identity (the base overload) and
    /// registers its collection manifest plus a namespace-scoped <see cref="IDocumentStore"/>.
    /// </summary>
    public static IServiceCollection AddModule(
        this IServiceCollection services, ModuleIdentity identity, string name, Action<ModuleManifestBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        ModuleManifestBuilder builder = new();
        configure(builder);
        ModuleManifest manifest = builder.Build();

        services.AddModule(identity, name); // base overload: authenticator + control-DB registration
        services.AddSingleton(manifest);

        // Keyed by module key so multiple modules can co-exist in one host: each module's stores resolve
        // the document store keyed to THEIR identity + manifest, instead of a single shared registration
        // where the last-installed module would win and scope every collection to its own manifest.
        services.AddKeyedSingleton<IDocumentStore>(identity.Key, (sp, _) => new ScopedDocumentStore(
            identity,
            manifest,
            sp.GetRequiredService<IClientDatabaseResolver>(),
            sp.GetRequiredService<ICurrentActor>(),
            sp.GetRequiredService<ModuleAccess>()));

        return services;
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
