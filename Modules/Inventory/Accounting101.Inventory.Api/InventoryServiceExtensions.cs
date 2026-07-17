using Accounting101.Inventory;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Hosting;
using Accounting101.Ledger.Contracts;
using Accounting101.ModuleKit.Api;

namespace Accounting101.Inventory.Api;

/// <summary>Installs the inventory module: identity + manifest (reference "items" + evidentiary
/// "stock-movements"), the document-store-backed item register and stock-movement stores, the
/// store-backed posting-accounts provider (per-client, config fallback), the movement service, and the
/// loopback ledger HttpClient (movements are the first slice that posts).</summary>
public static class InventoryServiceExtensions
{
    public static IServiceCollection AddInventory(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModule(new ModuleIdentity("inventory"), "Inventory", manifest =>
        {
            manifest.Reference("items");
            manifest.Evidentiary("stock-movements");
        });

        services.AddScoped<IItemStore>(sp =>
            new DocumentItemStore(sp.GetRequiredKeyedService<IDocumentStore>("inventory")));
        services.AddScoped<InventoryService>();

        services.AddScoped<IStockMovementStore>(sp =>
            new DocumentStockMovementStore(sp.GetRequiredKeyedService<IDocumentStore>("inventory")));
        services.AddScoped<InventoryMovementService>();
        services.AddScoped<ItemValuationService>();
        services.AddScoped<IInventoryAccountsProvider, StoreBackedInventoryAccountsProvider>();
        services.AddScoped<InventoryChartRequirements>();

        // Explicit client name to avoid the ILedgerClient short-name collision across modules.
        services.AddModuleLedgerClient<ILedgerClient, HttpLedgerClient>("InventoryLedgerClient", configuration);

        return services;
    }
}
