using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Control;
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
}
