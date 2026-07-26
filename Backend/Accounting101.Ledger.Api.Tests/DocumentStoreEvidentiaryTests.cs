using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Api.Documents;
using Accounting101.Ledger.Api.Tenancy;
using Accounting101.Ledger.Contracts;
using Accounting101.Ledger.Mongo;

namespace Accounting101.Ledger.Api.Tests;

file sealed class FixedActor(Guid userId) : ICurrentActor
{
    public Actor Get() => new() { UserId = userId, Name = "Tester" };
}

public sealed record Invoice(decimal Total);

public sealed class DocumentStoreEvidentiaryTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private async Task<(IDocumentStore store, Guid clientId)> SetupAsync()
    {
        ControlStore control = fixture.Control();
        await control.RegisterModuleAsync(new ModuleRegistration { Key = "invoicing", Name = "Invoicing", Enabled = true });
        SeededClient client = await fixture.SeedClientAsync();

        IDocumentStore store = new ScopedDocumentStore(
            new ModuleIdentity("invoicing"),
            new ModuleManifestBuilder().Evidentiary("invoices", "Customer", "Status").Build(),
            new ClientDatabaseResolver(fixture.Mongo, control),
            new FixedActor(client.UserId),
            new ModuleAccess(control));
        return (store, client.ClientId);
    }

    private static Dictionary<string, string> Tags(string customer, string status) =>
        new() { ["Customer"] = customer, ["Status"] = status };

    [Fact]
    public async Task Draft_is_mutable_then_finalize_assigns_a_gapless_number_and_locks_it()
    {
        (IDocumentStore store, Guid clientId) = await SetupAsync();
        string cust = Guid.NewGuid().ToString();

        Guid id = await store.CreateAsync(clientId, "invoices", new Invoice(100m), Tags(cust, "Draft"));
        await store.UpdateAsync(clientId, "invoices", id, new Invoice(120m), Tags(cust, "Draft")); // allowed while draft
        long number = await store.FinalizeAsync(clientId, "invoices", id);
        Assert.Equal(1, number);
        DocumentResult<Invoice>? fetched = await store.GetAsync<Invoice>(clientId, "invoices", id);
        Assert.Equal(number, fetched!.Sequence);

        Guid id2 = await store.CreateAsync(clientId, "invoices", new Invoice(50m), Tags(cust, "Draft"));
        Assert.Equal(2, await store.FinalizeAsync(clientId, "invoices", id2)); // gapless

        // Update after finalize is rejected.
        await Assert.ThrowsAsync<ModuleDocumentException>(() =>
            store.UpdateAsync(clientId, "invoices", id, new Invoice(999m), Tags(cust, "Draft")));
    }

    [Fact]
    public async Task Supersede_links_old_and_new_and_hides_the_old_from_query()
    {
        (IDocumentStore store, Guid clientId) = await SetupAsync();
        string cust = Guid.NewGuid().ToString();
        Guid id = await store.CreateAsync(clientId, "invoices", new Invoice(100m), Tags(cust, "Issued"));
        await store.FinalizeAsync(clientId, "invoices", id);

        Guid newId = await store.SupersedeAsync(clientId, "invoices", id, new Invoice(110m), Tags(cust, "Issued"));

        IReadOnlyList<DocumentResult<Invoice>> active = await store.QueryAsync<Invoice>(clientId, "invoices", Tags(cust, "Issued"));
        Assert.Single(active);
        Assert.Equal(110m, active[0].Body.Total);
        Assert.NotEqual(id, newId);
    }

    [Fact]
    public async Task Void_withdraws_a_finalized_document_with_no_successor()
    {
        (IDocumentStore store, Guid clientId) = await SetupAsync();
        string cust = Guid.NewGuid().ToString();
        Guid id = await store.CreateAsync(clientId, "invoices", new Invoice(100m), Tags(cust, "Issued"));
        await store.FinalizeAsync(clientId, "invoices", id);

        await store.VoidAsync(clientId, "invoices", id);

        IReadOnlyList<DocumentResult<Invoice>> active = await store.QueryAsync<Invoice>(clientId, "invoices", Tags(cust, "Issued"));
        Assert.Empty(active);
    }

    [Fact]
    public async Task Illegal_transitions_are_rejected()
    {
        (IDocumentStore store, Guid clientId) = await SetupAsync();
        string cust = Guid.NewGuid().ToString();
        Guid id = await store.CreateAsync(clientId, "invoices", new Invoice(1m), Tags(cust, "Draft"));

        // Supersede before finalize.
        await Assert.ThrowsAsync<ModuleDocumentException>(() =>
            store.SupersedeAsync(clientId, "invoices", id, new Invoice(2m), Tags(cust, "Draft")));

        await store.FinalizeAsync(clientId, "invoices", id);
        // Finalize twice.
        await Assert.ThrowsAsync<ModuleDocumentException>(() => store.FinalizeAsync(clientId, "invoices", id));
    }

    [Fact]
    public async Task NextNumber_is_a_standalone_gapless_counter()
    {
        (IDocumentStore store, Guid clientId) = await SetupAsync();
        Assert.Equal(1, await store.NextNumberAsync(clientId, "batch"));
        Assert.Equal(2, await store.NextNumberAsync(clientId, "batch"));
    }

    [Fact]
    public async Task Read_exposes_the_lifecycle_state()
    {
        (IDocumentStore store, Guid clientId) = await SetupAsync();
        string cust = Guid.NewGuid().ToString();
        Guid id = await store.CreateAsync(clientId, "invoices", new Invoice(100m), Tags(cust, "Draft"));

        Assert.Equal(DocumentLifecycle.Draft, (await store.GetAsync<Invoice>(clientId, "invoices", id))!.State);

        await store.FinalizeAsync(clientId, "invoices", id);
        Assert.Equal(DocumentLifecycle.Finalized, (await store.GetAsync<Invoice>(clientId, "invoices", id))!.State);

        await store.VoidAsync(clientId, "invoices", id);
        Assert.Equal(DocumentLifecycle.Voided, (await store.GetAsync<Invoice>(clientId, "invoices", id))!.State);
    }

    [Fact]
    public async Task CreateWithId_writes_under_the_supplied_id_and_returns_true()
    {
        (IDocumentStore store, Guid clientId) = await SetupAsync();
        string cust = Guid.NewGuid().ToString();
        Guid id = Guid.NewGuid();

        bool created = await store.CreateWithIdAsync(clientId, "invoices", id, new Invoice(10m), Tags(cust, "Draft"));

        Assert.True(created);
        DocumentResult<Invoice>? found = await store.GetAsync<Invoice>(clientId, "invoices", id);
        Assert.NotNull(found);
        Assert.Equal(id, found!.Id);
        Assert.Equal(DocumentLifecycle.Draft, found.State);
    }

    [Fact]
    public async Task CreateWithId_returns_false_when_the_id_is_taken_and_does_not_overwrite()
    {
        (IDocumentStore store, Guid clientId) = await SetupAsync();
        string cust = Guid.NewGuid().ToString();
        Guid id = Guid.NewGuid();
        await store.CreateWithIdAsync(clientId, "invoices", id, new Invoice(10m), Tags(cust, "Draft"));

        bool second = await store.CreateWithIdAsync(clientId, "invoices", id, new Invoice(999m), Tags(cust, "Draft"));

        Assert.False(second);
        DocumentResult<Invoice>? found = await store.GetAsync<Invoice>(clientId, "invoices", id);
        Assert.Equal(10m, found!.Body.Total); // the first body survived
    }

    [Fact]
    public async Task CreateWithId_refusal_burns_no_sequence_number()
    {
        (IDocumentStore store, Guid clientId) = await SetupAsync();
        string cust = Guid.NewGuid().ToString();
        Guid first = Guid.NewGuid();
        await store.CreateWithIdAsync(clientId, "invoices", first, new Invoice(10m), Tags(cust, "Draft"));
        long firstNumber = await store.FinalizeAsync(clientId, "invoices", first);

        // A refused create must not touch the counter.
        await store.CreateWithIdAsync(clientId, "invoices", first, new Invoice(10m), Tags(cust, "Draft"));

        Guid next = Guid.NewGuid();
        await store.CreateWithIdAsync(clientId, "invoices", next, new Invoice(20m), Tags(cust, "Draft"));
        Assert.Equal(firstNumber + 1, await store.FinalizeAsync(clientId, "invoices", next)); // gapless
    }
}
