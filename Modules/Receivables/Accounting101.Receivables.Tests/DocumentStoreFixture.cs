using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Api.Documents;
using Accounting101.Ledger.Api.Tenancy;
using Accounting101.Ledger.Contracts;
using Accounting101.Ledger.Mongo;
using Accounting101.TestSupport;
using EphemeralMongo;
using MongoDB.Driver;

namespace Accounting101.Receivables.Tests;

/// <summary>
/// A disposable EphemeralMongo instance wired up exactly as the host would wire the receivables module:
/// a registered client + member + the "receivables" module, and a real <see cref="ScopedDocumentStore"/>
/// bound to that identity with the receivables manifest. Repository tests get a real document store to
/// exercise the actual policy/lifecycle behavior.
/// </summary>
public sealed class DocumentStoreFixture : IAsyncLifetime
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
        });
        await control.AddMembershipAsync(UserId, ClientId, LedgerRole.Controller);
        await control.RegisterModuleAsync(new ModuleRegistration { Key = "receivables", Name = "Receivables", Enabled = true });

        ModuleManifest manifest = new ModuleManifestBuilder()
            .Reference("customers")
            .Plain("invoice-drafts")                 // drafts are scratch, freely edited/discarded
            .Evidentiary("invoices", "Customer")
            .Evidentiary("payments", "Customer")
            .Evidentiary("credit-applications", "Customer")
            .Build();

        Store = new ScopedDocumentStore(
            new ModuleIdentity("receivables"),
            manifest,
            new ClientDatabaseResolver(mongo, control),
            new FixedCurrentActor(UserId),
            new ModuleAccess(control));
    }

    public Task DisposeAsync() => Task.CompletedTask;
}

/// <summary>An <see cref="ICurrentActor"/> that always returns a fixed member principal.</summary>
internal sealed class FixedCurrentActor(Guid userId) : ICurrentActor
{
    public Actor Get() => new() { UserId = userId, Name = "Tester" };
}
