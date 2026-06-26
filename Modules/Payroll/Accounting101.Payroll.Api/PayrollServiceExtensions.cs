using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Hosting;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Payroll.Api;

/// <summary>Installs the payroll module into the host: module identity + collection manifest, the
/// document-store-backed stores and service, the config-backed accounts provider, and the loopback
/// ledger HttpClient. Payroll is the third installed module — the live test of N-module host
/// composition (each module's document store is keyed by its own module key, so their manifests do
/// not clobber one another).</summary>
public static class PayrollServiceExtensions
{
    public static IServiceCollection AddPayroll(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModule(new ModuleIdentity("payroll"), "Payroll", manifest =>
        {
            manifest.Evidentiary("payroll-runs");
            manifest.Evidentiary("tax-remittances");
        });

        services.AddScoped<IPayrollRunStore>(sp => new DocumentPayrollRunStore(sp.GetRequiredKeyedService<IDocumentStore>("payroll")));
        services.AddScoped<ITaxRemittanceStore>(sp => new DocumentTaxRemittanceStore(sp.GetRequiredKeyedService<IDocumentStore>("payroll")));
        services.AddScoped<PayrollService>();
        services.AddSingleton<IPayrollAccountsProvider, ConfiguredPayrollAccountsProvider>();

        // Use an explicit name to avoid a short-name collision with the other modules' ILedgerClient
        // (Payroll, Payables, and Invoicing all declare a type named "ILedgerClient"; the type-only
        // overload would key the HttpClient by that short name and last-wins-clobber the others).
        services.AddHttpClient("PayrollLedgerClient", client =>
                client.BaseAddress = new Uri(configuration["Engine:BaseAddress"] ?? "http://localhost"))
            .AddTypedClient<ILedgerClient, HttpLedgerClient>();

        return services;
    }
}
