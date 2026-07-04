using Accounting101.Ledger.Api.Hosting;
using Accounting101.Ledger.Api.Platform;
using Accounting101.TestSupport;
using EphemeralMongo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Tests;

public sealed class PlatformRegistryWiringTests
{
    [Fact]
    public async Task AddPlatformRegistry_seeds_default_cluster_and_factory_resolves_home_client()
    {
        IMongoRunner runner = await SharedMongo.InstanceAsync();
        string conn = runner.ConnectionString;
        string platformDb = "platform_" + Guid.NewGuid().ToString("N");

        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mongo:ConnectionString"] = conn,
            ["Mongo:PlatformDatabase"] = platformDb,
        }).Build();

        ServiceCollection services = new();
        services.AddSingleton<IMongoClient>(new MongoClient(conn));
        services.AddPlatformRegistry(config);
        await using ServiceProvider sp = services.BuildServiceProvider();

        // Run the hosted seeder(s), as the host would on startup.
        foreach (IHostedService hosted in sp.GetServices<IHostedService>())
            await hosted.StartAsync(CancellationToken.None);

        PlatformStore platform = sp.GetRequiredService<PlatformStore>();
        ClusterRegistration? def = await platform.GetClusterAsync("default");
        Assert.NotNull(def);
        Assert.Equal(conn, def!.ConnectionString);

        IMongoClientFactory factory = sp.GetRequiredService<IMongoClientFactory>();
        IMongoClient home = sp.GetRequiredService<IMongoClient>();
        Assert.Same(home, await factory.GetAsync("default"));
    }
}
