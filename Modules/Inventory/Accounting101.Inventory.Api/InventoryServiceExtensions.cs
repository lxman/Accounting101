using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Hosting;

namespace Accounting101.Inventory.Api;

/// <summary>Installs the inventory module: identity + manifest only (reference "items" + evidentiary
/// "stock-movements"). Scaffold task — no stores/services/posting yet; those arrive in later slices.</summary>
public static class InventoryServiceExtensions
{
    public static IServiceCollection AddInventory(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModule(new ModuleIdentity("inventory"), "Inventory", manifest =>
        {
            manifest.Reference("items");
            manifest.Evidentiary("stock-movements");
        });
        return services;
    }
}
