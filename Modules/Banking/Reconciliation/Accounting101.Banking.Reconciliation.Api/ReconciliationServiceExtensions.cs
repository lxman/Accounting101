using Accounting101.Banking.Reconciliation;
using Accounting101.Interchange;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Hosting;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Banking.Reconciliation.Api;

/// <summary>Installs the reconciliation module: module identity + collection manifest (evidentiary
/// bank-statements, plain reconciliations), the document-store-backed stores and service, and the
/// read-only loopback ledger client. Slice 1 posts nothing, so no accounts provider or module credential
/// is used.</summary>
public static class ReconciliationServiceExtensions
{
    public static IServiceCollection AddReconciliation(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModule(new ModuleIdentity("reconciliation"), "Reconciliation", manifest =>
        {
            manifest.Evidentiary("bank-statements");
            manifest.Plain("reconciliations");
            manifest.Evidentiary("bank-adjustments");
        });

        services.AddScoped<IBankStatementStore>(sp => new DocumentBankStatementStore(sp.GetRequiredKeyedService<IDocumentStore>("reconciliation")));
        services.AddScoped<IReconciliationStore>(sp => new DocumentReconciliationStore(sp.GetRequiredKeyedService<IDocumentStore>("reconciliation")));
        services.AddScoped<ReconciliationService>();

        // Read-only loopback client; explicit name avoids the cross-module ILedgerClient short-name collision.
        services.AddHttpClient("ReconciliationLedgerClient", client =>
                client.BaseAddress = new Uri(configuration["Engine:BaseAddress"] ?? "http://localhost"))
            .AddTypedClient<IReconciliationLedgerReader, HttpReconciliationLedgerReader>();

        services.AddScoped<IBankAdjustmentStore>(sp => new DocumentBankAdjustmentStore(sp.GetRequiredKeyedService<IDocumentStore>("reconciliation")));
        services.AddScoped<AdjustmentService>();

        // Credentialed posting client (Slice 3) — distinct named client from the Slice 1 read-only reader.
        services.AddHttpClient("ReconciliationPostingClient", client =>
                client.BaseAddress = new Uri(configuration["Engine:BaseAddress"] ?? "http://localhost"))
            .AddTypedClient<ILedgerClient, HttpLedgerClient>();

        // Import/export framework (Slice 4a) — the default registry (CSV statement importer registered).
        services.AddSingleton<IInterchangeRegistry>(InterchangeRegistry.CreateDefault());

        return services;
    }
}
