using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Api.Documents;
using Accounting101.Ledger.Api.Tenancy;
using Accounting101.Ledger.Contracts;
using Accounting101.Ledger.Mongo;
using Accounting101.Ledger.Mongo.Documents;

namespace Accounting101.Ledger.Api.Tests;

file sealed class FixedActor(Guid userId) : ICurrentActor
{
    public Actor Get() => new() { UserId = userId, Name = "Tester" };
}

public sealed record Customer(string Name);

public sealed class DocumentStoreReferenceTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private async Task<(IDocumentStore store, Guid clientId, MongoAuditLog audit)> SetupAsync()
    {
        ControlStore control = fixture.Control();
        await control.RegisterModuleAsync(new ModuleRegistration { Key = "invoicing", Name = "Invoicing", Enabled = true });
        SeededClient client = await fixture.SeedClientAsync();

        IDocumentStore store = new ScopedDocumentStore(
            new ModuleIdentity("invoicing"),
            new ModuleManifestBuilder().Reference("customers", "Status").Build(),
            new ClientDatabaseResolver(fixture.Mongo, control),
            new FixedActor(client.UserId),
            new ModuleAccess(control));

        MongoAuditLog audit = new(fixture.ClientDatabase(client.Database));
        return (store, client.ClientId, audit);
    }

    [Fact]
    public async Task Reference_put_is_recorded_on_the_audit_chain_and_create_vs_update_distinguished()
    {
        (IDocumentStore store, Guid clientId, MongoAuditLog audit) = await SetupAsync();
        Guid id = Guid.NewGuid();

        await store.PutAsync(clientId, "customers", id, new Customer("Acme"), new Dictionary<string, string> { ["Status"] = "Active" });
        await store.PutAsync(clientId, "customers", id, new Customer("Acme Inc"), new Dictionary<string, string> { ["Status"] = "Active" });

        IReadOnlyList<AuditRecordDocument> trail = await audit.GetForEntryAsync(clientId, id);
        Assert.Equal(2, trail.Count);
        Assert.Equal(AuditAction.DocumentCreated, trail[0].Action);
        Assert.Equal(AuditAction.DocumentUpdated, trail[1].Action);
        Assert.True(await audit.VerifyAsync(clientId));
    }

    [Fact]
    public async Task Deactivate_soft_hides_the_document_and_is_audited()
    {
        (IDocumentStore store, Guid clientId, MongoAuditLog audit) = await SetupAsync();
        Guid id = Guid.NewGuid();
        await store.PutAsync(clientId, "customers", id, new Customer("Acme"), new Dictionary<string, string> { ["Status"] = "Active" });

        await store.DeactivateAsync(clientId, "customers", id);

        // soft: still gettable by id, but hidden from query
        Assert.NotNull(await store.GetAsync<Customer>(clientId, "customers", id));
        IReadOnlyList<DocumentResult<Customer>> active = await store.QueryAsync<Customer>(clientId, "customers", new Dictionary<string, string> { ["Status"] = "Active" });
        Assert.Empty(active);

        IReadOnlyList<AuditRecordDocument> trail = await audit.GetForEntryAsync(clientId, id);
        Assert.Contains(trail, r => r.Action == AuditAction.DocumentDeactivated);
    }
}
