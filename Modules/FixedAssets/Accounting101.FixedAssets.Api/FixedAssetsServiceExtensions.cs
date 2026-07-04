using Accounting101.FixedAssets;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Hosting;
using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets.Api;

/// <summary>Installs the fixed-assets module: identity + manifest (reference "assets" + evidentiary
/// "depreciation-runs"), the document-store-backed stores, the depreciation strategies + selector, the
/// config-backed posting-accounts provider, the run service, and the loopback ledger HttpClient (FA-2 is
/// the first slice that posts).</summary>
public static class FixedAssetsServiceExtensions
{
    public static IServiceCollection AddFixedAssets(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModule(new ModuleIdentity("fixedassets"), "Fixed Assets", manifest =>
        {
            manifest.Reference("assets");
            manifest.Evidentiary("depreciation-runs");
        });

        services.AddScoped<IAssetStore>(sp =>
            new DocumentAssetStore(sp.GetRequiredKeyedService<IDocumentStore>("fixedassets")));
        services.AddScoped<IDepreciationRunStore>(sp =>
            new DocumentDepreciationRunStore(sp.GetRequiredKeyedService<IDocumentStore>("fixedassets")));
        services.AddScoped<FixedAssetsService>();
        services.AddScoped<FixedAssetsRunService>();

        services.AddSingleton<IDepreciationMethod, StraightLineDepreciation>();
        services.AddSingleton<IDepreciationMethod, DecliningBalanceDepreciation>();
        services.AddSingleton(sp => new DepreciationMethodSelector(sp.GetServices<IDepreciationMethod>()));
        services.AddSingleton<IFixedAssetsAccountsProvider, ConfiguredFixedAssetsAccountsProvider>();

        // Explicit client name to avoid the ILedgerClient short-name collision across modules.
        services.AddHttpClient("FixedAssetsLedgerClient", client =>
                client.BaseAddress = new Uri(configuration["Engine:BaseAddress"] ?? "http://localhost"))
            .AddTypedClient<ILedgerClient, HttpLedgerClient>();

        return services;
    }
}
