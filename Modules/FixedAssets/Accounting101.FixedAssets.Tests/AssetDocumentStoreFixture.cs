using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Api.Documents;
using Accounting101.Ledger.Api.Tenancy;
using Accounting101.Ledger.Contracts;
using Accounting101.Ledger.Mongo;
using Accounting101.TestSupport;
using EphemeralMongo;
using MongoDB.Driver;

namespace Accounting101.FixedAssets.Tests;

/// <summary>A disposable EphemeralMongo instance wired exactly as the host wires the fixed-assets module:
/// a registered client + member + the "fixedassets" module, and a real ScopedDocumentStore bound to that
/// identity with a .Reference("assets") manifest. Store tests exercise the actual reference policy.</summary>
public sealed class AssetDocumentStoreFixture : IAsyncLifetime
{
    public Guid ClientId { get; private set; }
    public Guid UserId { get; private set; }
    public IDocumentStore Store { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        IMongoRunner runner = await SharedMongo.InstanceAsync();
        IMongoClient mongo = new MongoClient(runner.ConnectionString);

        ControlStore control = new(mongo.GetDatabase("control_" + Guid.NewGuid().ToString("N")));
        ClientId = Guid.NewGuid();
        UserId = Guid.NewGuid();
        await control.RegisterClientAsync(new ClientRegistration
        {
            Id = ClientId, Name = "Acme", DatabaseName = "client_" + ClientId.ToString("N"),
            EnabledModules = ["fixedassets"],
        });
        await control.AddMembershipAsync(UserId, ClientId, LedgerRole.Controller);
        await control.RegisterModuleAsync(new ModuleRegistration { Key = "fixedassets", Name = "Fixed Assets", Enabled = true });

        ModuleManifest manifest = new ModuleManifestBuilder().Reference("assets").Build();

        Store = new ScopedDocumentStore(
            new ModuleIdentity("fixedassets"),
            manifest,
            new ClientDatabaseResolver(mongo, control),
            new FixedActor(UserId),
            new ModuleAccess(control));
    }

    public Task DisposeAsync() => Task.CompletedTask;
}

/// <summary>An ICurrentActor that always returns a fixed member principal.</summary>
internal sealed class FixedActor(Guid userId) : ICurrentActor
{
    public Actor Get() => new() { UserId = userId, Name = "Tester" };
}
