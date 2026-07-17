using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Hosting;
using Accounting101.Ledger.Contracts;
using Accounting101.ModuleKit.Api;

namespace Accounting101.Payables.Api;

/// <summary>Installs the payables module into the host: module identity + collection manifest, the
/// document-store-backed stores and services, the store-backed accounts provider (per-client posting
/// accounts, with config fallback), and the loopback ledger HttpClient.</summary>
public static class PayablesServiceExtensions
{
    public static IServiceCollection AddPayables(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModule(new ModuleIdentity("payables"), "Payables", manifest =>
        {
            manifest.Reference("vendors");
            manifest.Plain("bill-drafts");                 // drafts are scratch — editable, discardable
            manifest.Evidentiary("bills", "Vendor");
            manifest.Evidentiary("bill-payments", "Vendor");
            manifest.Evidentiary("vendor-credit-applications", "Vendor");
        });

        services.AddScoped<IVendorStore>(sp => new DocumentVendorStore(sp.GetRequiredKeyedService<IDocumentStore>("payables")));
        services.AddScoped<IBillStore>(sp => new DocumentBillStore(sp.GetRequiredKeyedService<IDocumentStore>("payables")));
        services.AddScoped<IBillPaymentStore>(sp => new DocumentBillPaymentStore(sp.GetRequiredKeyedService<IDocumentStore>("payables")));
        services.AddScoped<BillService>();
        services.AddScoped<BillPaymentService>();
        services.AddScoped<VendorAccountService>();
        services.AddScoped<IBillAccountsProvider, StoreBackedBillAccountsProvider>();
        services.AddScoped<PayablesChartRequirements>();

        // Use an explicit name to avoid a short-name collision with Accounting101.Invoicing.ILedgerClient
        // (both are named "ILedgerClient" by the factory when using the type-only overload).
        services.AddModuleLedgerClient<ILedgerClient, HttpLedgerClient>("PayablesLedgerClient", configuration);

        return services;
    }
}
