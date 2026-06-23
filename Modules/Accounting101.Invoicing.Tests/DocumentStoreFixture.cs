using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Api.Documents;
using Accounting101.Ledger.Api.Tenancy;
using Accounting101.Ledger.Contracts;
using Accounting101.Ledger.Mongo;
using EphemeralMongo;
using MongoDB.Driver;

namespace Accounting101.Invoicing.Tests;

/// <summary>
/// A disposable EphemeralMongo instance wired up exactly as the host would wire the invoicing module:
/// a registered client + member + the "invoicing" module, and a real <see cref="ScopedDocumentStore"/>
/// bound to that identity with the invoicing manifest. Repository tests get a real document store to
/// exercise the actual policy/lifecycle behavior.
/// </summary>
public sealed class DocumentStoreFixture : IAsyncLifetime
{
    private IMongoRunner _runner = null!;

    public Guid ClientId { get; private set; }
    public Guid UserId { get; private set; }
    public IDocumentStore Store { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        MongoRunnerOptions options = new()
        {
            Version = MongoVersion.V8,
            UseSingleNodeReplicaSet = true,
            AdditionalArguments = ["--quiet"],
            StandardErrorLogger = Console.Error.WriteLine,
        };
        _runner = await MongoRunner.RunAsync(options);
        IMongoClient mongo = new MongoClient(_runner.ConnectionString);

        ControlStore control = new(mongo.GetDatabase("control_" + Guid.NewGuid().ToString("N")));
        ClientId = Guid.NewGuid();
        UserId = Guid.NewGuid();
        await control.RegisterClientAsync(new ClientRegistration
        {
            Id = ClientId, Name = "Acme", DatabaseName = "client_" + ClientId.ToString("N"),
        });
        await control.AddMembershipAsync(UserId, ClientId, LedgerRole.Controller);
        await control.RegisterModuleAsync(new ModuleRegistration { Key = "invoicing", Name = "Invoicing", Enabled = true });

        ModuleManifest manifest = new ModuleManifestBuilder()
            .Reference("customers")
            .Evidentiary("invoices", "Customer")
            .Build();

        Store = new ScopedDocumentStore(
            new ModuleIdentity("invoicing"),
            manifest,
            new ClientDatabaseResolver(mongo, control),
            new FixedCurrentActor(UserId),
            new ModuleAccess(control));
    }

    public Task DisposeAsync()
    {
        _runner?.Dispose();
        return Task.CompletedTask;
    }
}

/// <summary>An <see cref="ICurrentActor"/> that always returns a fixed member principal.</summary>
internal sealed class FixedCurrentActor(Guid userId) : ICurrentActor
{
    public Actor Get() => new() { UserId = userId, Name = "Tester" };
}
