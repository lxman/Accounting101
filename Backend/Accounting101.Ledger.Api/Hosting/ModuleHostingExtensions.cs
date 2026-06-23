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

        services.AddSingleton(identity);
        services.AddSingleton<IModuleAuthenticator>(new HostStampedModuleAuthenticator(identity));
        services.AddSingleton(new ModuleRegistration { Key = identity.Key, Name = name, Enabled = true });

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
        services.AddSingleton<IDocumentStore>(sp => new ScopedDocumentStore(
            identity,
            manifest,
            sp.GetRequiredService<IClientDatabaseResolver>(),
            sp.GetRequiredService<ICurrentActor>(),
            sp.GetRequiredService<ModuleAccess>()));

        return services;
    }
}
