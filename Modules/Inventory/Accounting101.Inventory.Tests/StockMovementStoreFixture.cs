using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Api.Documents;
using Accounting101.Ledger.Api.Tenancy;
using Accounting101.Ledger.Contracts;
using Accounting101.Ledger.Mongo;
using Accounting101.TestSupport;
using EphemeralMongo;
using MongoDB.Driver;

namespace Accounting101.Inventory.Tests;

/// <summary>A disposable EphemeralMongo instance wired exactly as the host wires the inventory module:
/// a registered client + member + the "inventory" module, and a real ScopedDocumentStore bound to that
/// identity with an .Evidentiary("stock-movements") manifest. Store tests exercise the actual
/// evidentiary numbering + lifecycle policy.</summary>
public sealed class StockMovementStoreFixture : IAsyncLifetime
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
            EnabledModules = ["inventory"],
        });
        await control.AddMembershipAsync(UserId, ClientId, LedgerRole.Controller);
        await control.RegisterModuleAsync(new ModuleRegistration { Key = "inventory", Name = "Inventory", Enabled = true });

        ModuleManifest manifest = new ModuleManifestBuilder().Evidentiary("stock-movements").Build();

        Store = new ScopedDocumentStore(
            new ModuleIdentity("inventory"),
            manifest,
            new ClientDatabaseResolver(mongo, control),
            new FixedActor(UserId),
            new ModuleAccess(control));
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
