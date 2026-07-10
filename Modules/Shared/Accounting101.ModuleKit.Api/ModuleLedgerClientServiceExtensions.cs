using Accounting101.ModuleKit.Api;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Accounting101.ModuleKit.Api;

public static class ModuleLedgerClientServiceExtensions
{
    /// <summary>
    /// Registers a module's named loopback ledger HttpClient (based at <c>Engine:BaseAddress</c>) and
    /// binds it to the module's typed client. The explicit <paramref name="httpClientName"/> avoids the
    /// ILedgerClient short-name collision across modules. The keyed <c>ModuleCredential</c> is registered
    /// separately by the module's <c>AddModule</c> call and resolved via <c>[FromKeyedServices]</c>.
    /// </summary>
    public static IServiceCollection AddModuleLedgerClient<TInterface, TClient>(
        this IServiceCollection services, string httpClientName, IConfiguration configuration)
        where TInterface : class
        where TClient : ModuleLedgerClient, TInterface
    {
        services.AddHttpClient(httpClientName, client =>
                client.BaseAddress = new Uri(configuration["Engine:BaseAddress"] ?? "http://localhost"))
            .AddTypedClient<TInterface, TClient>();
        return services;
    }
}
