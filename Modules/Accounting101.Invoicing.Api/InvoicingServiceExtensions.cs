using Accounting101.Invoicing;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Hosting;

namespace Accounting101.Invoicing.Api;

/// <summary>
/// Installs the invoicing module into the host: stamps its module identity + collection manifest,
/// wires its document-store-backed stores and service, the config-backed accounts provider, and the
/// loopback ledger HttpClient. "Installed" = this line is present in the host's composition root.
/// </summary>
public static class InvoicingServiceExtensions
{
    public static IServiceCollection AddInvoicing(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModule(new ModuleIdentity("invoicing"), "Invoicing", manifest =>
        {
            manifest.Reference("customers");
            manifest.Evidentiary("invoices", "Customer");
            manifest.Evidentiary("payments", "Customer");
            manifest.Evidentiary("credit-applications", "Customer");
        });

        services.AddScoped<ICustomerStore, DocumentCustomerStore>();
        services.AddScoped<IInvoiceStore, DocumentInvoiceStore>();
        services.AddScoped<IPaymentStore, DocumentPaymentStore>();
        services.AddScoped<InvoiceService>();
        services.AddScoped<PaymentService>();
        services.AddSingleton<IInvoiceAccountsProvider, ConfiguredInvoiceAccountsProvider>();
        services.AddSingleton<IPaymentAccountsProvider, ConfiguredPaymentAccountsProvider>();

        services.AddHttpClient<ILedgerClient, HttpLedgerClient>(client =>
            client.BaseAddress = new Uri(configuration["Engine:BaseAddress"] ?? "http://localhost"));

        return services;
    }
}
