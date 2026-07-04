using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Api.Platform;
using Accounting101.Ledger.Api.Tenancy;
using Accounting101.TestSupport;
using EphemeralMongo;
using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Tests;

public sealed class FirmScopedClientDatabaseResolverTests
{
    private static async Task<(FirmScope Scope, ControlStore Control, IMongoClientFactory Factory)> FirmAsync()
    {
        IMongoRunner runner = await SharedMongo.InstanceAsync();
        IMongoClient home = new MongoClient(runner.ConnectionString);
        PlatformStore platform = new(home.GetDatabase("platform_" + Guid.NewGuid().ToString("N")));
        await platform.RegisterClusterAsync(new ClusterRegistration { Key = "default", ConnectionString = runner.ConnectionString });
        MongoClientFactory factory = new(home, "default", platform);

        Guid firmId = Guid.NewGuid();
        string controlDb = "firm_" + firmId.ToString("N") + "_control";
        FirmRegistration firm = new() { Id = firmId, Name = "F", ControlDatabase = controlDb, ClusterKey = "default" };
        FirmScope scope = new() { Firm = firm, ControlDatabase = home.GetDatabase(controlDb) };
        ControlStore control = new(home.GetDatabase(controlDb));
        return (scope, control, factory);
    }

    [Fact]
    public async Task Resolves_a_client_registered_in_this_firm()
    {
        (FirmScope scope, ControlStore control, IMongoClientFactory factory) = await FirmAsync();
        Guid clientId = Guid.NewGuid();
        string clientDb = "firm_client_" + clientId.ToString("N");
        await control.RegisterClientAsync(new ClientRegistration { Id = clientId, Name = "C", DatabaseName = clientDb });

        FirmScopedClientDatabaseResolver resolver = new(scope, control, factory);
        IMongoDatabase? db = await resolver.ResolveAsync(clientId);

        Assert.NotNull(db);
        Assert.Equal(clientDb, db!.DatabaseNamespace.DatabaseName);
    }

    [Fact]
    public async Task Refuses_a_client_not_registered_in_this_firm()
    {
        (FirmScope scope, ControlStore control, IMongoClientFactory factory) = await FirmAsync();

        FirmScopedClientDatabaseResolver resolver = new(scope, control, factory);
        // A clientId that belongs to no client in this firm's registry (e.g. another firm's client).
        Assert.Null(await resolver.ResolveAsync(Guid.NewGuid()));
    }

    [Fact]
    public void Index_guard_claims_once_per_client()
    {
        IndexGuard guard = new();
        Guid clientId = Guid.NewGuid();
        Assert.True(guard.TryClaim(clientId));
        Assert.False(guard.TryClaim(clientId));
        guard.Release(clientId);
        Assert.True(guard.TryClaim(clientId));
    }
}
