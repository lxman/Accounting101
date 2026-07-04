using Accounting101.FixedAssets;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Hosting;
using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets.Api;

/// <summary>Installs the fixed-assets module: module identity + a .Reference("assets") manifest, the
/// document-store-backed asset store, and the service. FA-1 does not post, so there is no ledger client.</summary>
public static class FixedAssetsServiceExtensions
{
    public static IServiceCollection AddFixedAssets(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModule(new ModuleIdentity("fixedassets"), "Fixed Assets", manifest =>
        {
            manifest.Reference("assets");
        });

        services.AddScoped<IAssetStore>(sp =>
            new DocumentAssetStore(sp.GetRequiredKeyedService<IDocumentStore>("fixedassets")));
        services.AddScoped<FixedAssetsService>();

        return services;
    }
}
