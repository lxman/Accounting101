using Accounting101.Ledger.Api.Platform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Hosting;

/// <summary>
/// Registers the platform-registry tier: the <see cref="PlatformStore"/> over the platform control DB,
/// the cluster-keyed <see cref="IMongoClientFactory"/>, and the startup seeder for the home cluster.
/// Additive — it does not alter how a request resolves a client database (that is a later phase).
/// </summary>
public static class PlatformRegistryExtensions
{
    public static IServiceCollection AddPlatformRegistry(this IServiceCollection services, IConfiguration configuration)
    {
        string platformDatabase = configuration["Mongo:PlatformDatabase"] ?? "platform_control";
        string homeClusterKey = configuration["Mongo:ClusterKey"] ?? "default";

        // PlatformClusterSeeder resolves IConfiguration from DI; ensure it is registered even when the
        // caller's container (e.g. a bare ServiceCollection in tests) has not already added it.
        services.AddSingleton(configuration);

        services.AddSingleton(sp =>
            new PlatformStore(sp.GetRequiredService<IMongoClient>().GetDatabase(platformDatabase)));

        services.AddSingleton<IMongoClientFactory>(sp =>
            new MongoClientFactory(
                sp.GetRequiredService<IMongoClient>(),
                homeClusterKey,
                sp.GetRequiredService<PlatformStore>()));

        services.AddHostedService<PlatformClusterSeeder>();

        return services;
    }
}
