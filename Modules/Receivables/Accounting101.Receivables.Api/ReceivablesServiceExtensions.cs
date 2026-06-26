using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Hosting;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Receivables.Api;

/// <summary>
/// Installs the receivables module into the host: stamps its module identity + collection manifest,
/// wires its document-store-backed stores and service, the config-backed accounts provider, and the
/// loopback ledger HttpClient. "Installed" = this line is present in the host's composition root.
/// </summary>
public static class ReceivablesServiceExtensions
{
    public static IServiceCollection AddReceivables(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModule(new ModuleIdentity("receivables"), "Receivables", manifest =>
        {
            manifest.Reference("customers");
            manifest.Plain("invoice-drafts");                 // drafts are scratch, freely edited/discarded
            manifest.Evidentiary("invoices", "Customer");
            manifest.Evidentiary("payments", "Customer");
            manifest.Evidentiary("credit-applications", "Customer");
            manifest.Evidentiary("write-offs", "Customer");
            manifest.Evidentiary("credit-notes", "Customer");
            manifest.Evidentiary("refunds", "Customer");
        });

        services.AddScoped<ICustomerStore>(sp => new DocumentCustomerStore(sp.GetRequiredKeyedService<IDocumentStore>("receivables")));
        services.AddScoped<IInvoiceStore>(sp => new DocumentInvoiceStore(sp.GetRequiredKeyedService<IDocumentStore>("receivables")));
        services.AddScoped<IPaymentStore>(sp => new DocumentPaymentStore(sp.GetRequiredKeyedService<IDocumentStore>("receivables")));
        services.AddScoped<InvoiceService>();
        services.AddScoped<PaymentService>();
        services.AddSingleton<IInvoiceAccountsProvider, ConfiguredInvoiceAccountsProvider>();
        services.AddSingleton<IPaymentAccountsProvider, ConfiguredPaymentAccountsProvider>();

        services.AddHttpClient<ILedgerClient, HttpLedgerClient>(client =>
            client.BaseAddress = new Uri(configuration["Engine:BaseAddress"] ?? "http://localhost"));

        return services;
    }
}
