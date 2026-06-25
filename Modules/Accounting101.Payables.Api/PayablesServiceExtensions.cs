using Accounting101.Payables;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Hosting;

namespace Accounting101.Payables.Api;

/// <summary>Installs the payables module into the host: module identity + collection manifest, the
/// document-store-backed stores and services, the config-backed accounts provider, and the loopback
/// ledger HttpClient.</summary>
public static class PayablesServiceExtensions
{
    public static IServiceCollection AddPayables(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModule(new ModuleIdentity("payables"), "Payables", manifest =>
        {
            manifest.Reference("vendors");
            manifest.Evidentiary("bills", "Vendor");
            manifest.Evidentiary("bill-payments", "Vendor");
            manifest.Evidentiary("vendor-credit-applications", "Vendor");
        });

        services.AddScoped<IVendorStore, DocumentVendorStore>();
        services.AddScoped<IBillStore, DocumentBillStore>();
        services.AddScoped<IBillPaymentStore, DocumentBillPaymentStore>();
        services.AddScoped<BillService>();
        services.AddScoped<BillPaymentService>();
        services.AddSingleton<IBillAccountsProvider, ConfiguredBillAccountsProvider>();

        // Use an explicit name to avoid a short-name collision with Accounting101.Invoicing.ILedgerClient
        // (both are named "ILedgerClient" by the factory when using the type-only overload).
        services.AddHttpClient("PayablesLedgerClient", client =>
                client.BaseAddress = new Uri(configuration["Engine:BaseAddress"] ?? "http://localhost"))
            .AddTypedClient<ILedgerClient, HttpLedgerClient>();

        return services;
    }
}
