using Accounting101.Inventory;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Hosting;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Inventory.Api;

/// <summary>Installs the inventory module: identity + manifest (reference "items" + evidentiary
/// "stock-movements"), and the document-store-backed item register (store + service). Movement
/// logic/posting arrives in later slices.</summary>
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

        return services;
    }
}
