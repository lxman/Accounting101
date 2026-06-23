using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Api.Documents;
using Accounting101.Ledger.Api.Tenancy;
using Accounting101.Ledger.Contracts;
using Accounting101.Ledger.Mongo;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>A fixed acting principal, so the store's authorization runs against a known user id.</summary>
file sealed class FixedActor(Guid userId) : ICurrentActor
{
    public Actor Get() => new() { UserId = userId, Name = "Tester" };
}

public sealed record Note(string Text); // a module's opaque document body

public sealed class DocumentStorePlainTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private static readonly ModuleIdentity Invoicing = new("invoicing");

    private static ModuleManifest Manifest() =>
        new ModuleManifestBuilder().Plain("notes").Build();

    private async Task<(IDocumentStore store, Guid clientId)> StoreForMemberAsync()
    {
        ControlStore control = fixture.Control();
        await control.RegisterModuleAsync(new ModuleRegistration { Key = "invoicing", Name = "Invoicing", Enabled = true });
        SeededClient client = await fixture.SeedClientAsync();

        IDocumentStore store = new ScopedDocumentStore(
            Invoicing, Manifest(),
            new ClientDatabaseResolver(fixture.Mongo, control),
            new FixedActor(client.UserId),
            new ModuleAccess(control));
        return (store, client.ClientId);
    }

    [Fact]
    public async Task Put_then_Get_round_trips_a_typed_body()
    {
        (IDocumentStore store, Guid clientId) = await StoreForMemberAsync();
        Guid id = Guid.NewGuid();

        await store.PutAsync(clientId, "notes", id, new Note("hello"), new Dictionary<string, string> { ["K"] = "v" });
        DocumentResult<Note>? read = await store.GetAsync<Note>(clientId, "notes", id);

        Assert.Equal("hello", read!.Body.Text);
    }

    [Fact]
    public async Task Query_finds_by_tag_and_Delete_removes()
    {
        (IDocumentStore store, Guid clientId) = await StoreForMemberAsync();
        Guid id = Guid.NewGuid();
        await store.PutAsync(clientId, "notes", id, new Note("x"), new Dictionary<string, string> { ["K"] = "find" });

        IReadOnlyList<DocumentResult<Note>> hits = await store.QueryAsync<Note>(clientId, "notes", new Dictionary<string, string> { ["K"] = "find" });
        Assert.Single(hits);

        await store.DeleteAsync(clientId, "notes", id);
        Assert.Null(await store.GetAsync<Note>(clientId, "notes", id));
    }

    [Fact]
    public async Task A_non_member_user_is_denied()
    {
        ControlStore control = fixture.Control();
        await control.RegisterModuleAsync(new ModuleRegistration { Key = "invoicing", Name = "Invoicing", Enabled = true });
        SeededClient client = await fixture.SeedClientAsync();

        IDocumentStore store = new ScopedDocumentStore(
            Invoicing, Manifest(),
            new ClientDatabaseResolver(fixture.Mongo, control),
            new FixedActor(Guid.NewGuid()), // NOT the seeded member
            new ModuleAccess(control));

        await Assert.ThrowsAsync<ModuleAccessDeniedException>(() =>
            store.PutAsync(client.ClientId, "notes", Guid.NewGuid(), new Note("x"), new Dictionary<string, string>()));
    }

    [Fact]
    public async Task An_undeclared_collection_is_rejected()
    {
        (IDocumentStore store, Guid clientId) = await StoreForMemberAsync();
        await Assert.ThrowsAsync<ModuleDocumentException>(() =>
            store.GetAsync<Note>(clientId, "ghosts", Guid.NewGuid()));
    }
}
