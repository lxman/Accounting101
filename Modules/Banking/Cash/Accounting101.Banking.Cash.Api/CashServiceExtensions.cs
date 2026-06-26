using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Hosting;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Banking.Cash.Api;

/// <summary>Installs the cash module into the host: module identity + collection manifest, the
/// document-store-backed stores and service, the config-backed accounts provider, and the loopback
/// ledger HttpClient. Cash is the fourth installed module — the live test of five-module host
/// composition (each module's document store is keyed by its own module key, so their manifests do
/// not clobber one another).</summary>
public static class CashServiceExtensions
{
    public static IServiceCollection AddCash(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModule(new ModuleIdentity("cash"), "Cash", manifest =>
        {
            manifest.Evidentiary("cash-disbursements");
            manifest.Evidentiary("cash-deposits");
        });

        services.AddScoped<ICashDisbursementStore>(sp => new DocumentCashDisbursementStore(sp.GetRequiredKeyedService<IDocumentStore>("cash")));
        services.AddScoped<ICashDepositStore>(sp => new DocumentCashDepositStore(sp.GetRequiredKeyedService<IDocumentStore>("cash")));
        services.AddScoped<CashService>();
        services.AddSingleton<ICashAccountsProvider, ConfiguredCashAccountsProvider>();

        // Use an explicit name to avoid a short-name collision with the other modules' ILedgerClient
        // (Cash, Payroll, Payables, and Invoicing all declare a type named "ILedgerClient"; the type-only
        // overload would key the HttpClient by that short name and last-wins-clobber the others).
        services.AddHttpClient("CashLedgerClient", client =>
                client.BaseAddress = new Uri(configuration["Engine:BaseAddress"] ?? "http://localhost"))
            .AddTypedClient<ILedgerClient, HttpLedgerClient>();

        return services;
    }
}
