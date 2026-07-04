using Accounting101.Ledger.Api.Platform;
using Accounting101.TestSupport;
using EphemeralMongo;
using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Tests;

public sealed class MongoClientFactoryTests
{
    [Fact]
    public async Task Home_key_returns_process_client_registered_key_is_cached_unknown_throws()
    {
        IMongoRunner runner = await SharedMongo.InstanceAsync();
        string conn = runner.ConnectionString;
        IMongoClient home = new MongoClient(conn);

        PlatformStore platform = new(home.GetDatabase("platform_" + Guid.NewGuid().ToString("N")));
        await platform.RegisterClusterAsync(new ClusterRegistration { Key = "cluster-2", ConnectionString = conn });

        MongoClientFactory factory = new(home, "default", platform);

        // Home key returns the exact process client (no second pool to the same server).
        Assert.Same(home, await factory.GetAsync("default"));

        // A registered non-home cluster gets its own client, cached across calls.
        IMongoClient a = await factory.GetAsync("cluster-2");
        IMongoClient b = await factory.GetAsync("cluster-2");
        Assert.Same(a, b);
        Assert.NotSame(home, a);

        // An unregistered key fails closed.
        await Assert.ThrowsAsync<InvalidOperationException>(() => factory.GetAsync("nope"));
    }

    [Fact]
    public async Task Null_or_blank_key_throws_ArgumentException()
    {
        IMongoRunner runner = await SharedMongo.InstanceAsync();
        IMongoClient home = new MongoClient(runner.ConnectionString);
        PlatformStore platform = new(home.GetDatabase("platform_" + Guid.NewGuid().ToString("N")));
        MongoClientFactory factory = new(home, "default", platform);

        await Assert.ThrowsAsync<ArgumentNullException>(() => factory.GetAsync(null!));
        await Assert.ThrowsAsync<ArgumentException>(() => factory.GetAsync("   "));
    }
}
