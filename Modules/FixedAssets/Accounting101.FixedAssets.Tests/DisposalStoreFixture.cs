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

/// <summary>Disposable EphemeralMongo wired for the disposals collection: a ScopedDocumentStore bound to
/// the "fixedassets" identity with the disposals evidentiary manifest. NewClient() registers a fresh
/// client per call so by-asset / count assertions are isolated.</summary>
public sealed class DisposalStoreFixture : IAsyncLifetime
{
    private ControlStore _control = null!;
    public IDocumentStore Store { get; private set; } = null!;
    private Guid UserId { get; set; }

    public async Task InitializeAsync()
    {
        IMongoRunner runner = await SharedMongo.InstanceAsync();
        IMongoClient mongo = new MongoClient(runner.ConnectionString);

        _control = new ControlStore(mongo.GetDatabase("control_" + Guid.NewGuid().ToString("N")));
        UserId = Guid.NewGuid();
        await _control.RegisterModuleAsync(new ModuleRegistration { Key = "fixedassets", Name = "Fixed Assets", Enabled = true });

        ModuleManifest manifest = new ModuleManifestBuilder()
            .Reference("assets").Evidentiary("depreciation-runs").Evidentiary("disposals").Build();
        Store = new ScopedDocumentStore(
            new ModuleIdentity("fixedassets"),
            manifest,
            new ClientDatabaseResolver(mongo, _control),
            new FixedActor(UserId),
            new ModuleAccess(_control));
    }

    public Guid NewClient()
    {
        Guid clientId = Guid.NewGuid();
        _control.RegisterClientAsync(new ClientRegistration
        {
            Id = clientId, Name = "Acme", DatabaseName = "client_" + clientId.ToString("N"),
            EnabledModules = ["fixedassets"],
        }).GetAwaiter().GetResult();
        _control.AddMembershipAsync(UserId, clientId, LedgerRole.Controller).GetAwaiter().GetResult();
        return clientId;
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
