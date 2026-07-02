using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Api.Documents;
using Accounting101.Ledger.Api.Tenancy;
using Accounting101.Ledger.Contracts;
using Accounting101.Ledger.Mongo;
using Accounting101.Receivables.Api;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>
/// Slice E: the document store and the receivables HTTP surface enforce ar.read/ar.write. An auditor
/// (ar.read, no ar.write) may read but not write; the write refusal surfaces as HTTP 403, not 500.
/// </summary>
public sealed class ModuleCapabilityEnforcementTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private static readonly ModuleIdentity Receivables = new("receivables");

    private static ModuleManifest Manifest() =>
        new ModuleManifestBuilder().Reference("customers").Build();

    private sealed class FixedActor(Guid userId) : ICurrentActor
    {
        public Actor Get() => new() { UserId = userId, Name = "Tester" };
    }

    public sealed record Party(string Name);

    private ScopedDocumentStore StoreFor(Guid userId, ControlStore control) =>
        new(Receivables, Manifest(), new ClientDatabaseResolver(fixture.Mongo, control), new FixedActor(userId), new ModuleAccess(control));

    [Fact]
    public async Task Auditor_write_to_the_document_store_is_denied_but_read_is_allowed()
    {
        ControlStore control = fixture.Control();
        await control.RegisterModuleAsync(new ModuleRegistration { Key = "receivables", Name = "Receivables", Enabled = true });
        SeededClient client = await fixture.SeedClientAsync(role: LedgerRole.Auditor);
        ScopedDocumentStore store = StoreFor(client.UserId, control);

        await Assert.ThrowsAsync<ModuleAccessDeniedException>(() =>
            store.PutAsync(client.ClientId, "customers", Guid.NewGuid(), new Party("Acme"), new Dictionary<string, string>()));

        // Read must NOT throw (auditor holds ar.read).
        IReadOnlyList<DocumentResult<Party>> hits =
            await store.QueryAsync<Party>(client.ClientId, "customers", new Dictionary<string, string>());
        Assert.Empty(hits);
    }

    [Fact]
    public async Task Auditor_creating_a_customer_over_http_gets_403()
    {
        ControlStore control = fixture.Control();
        await control.RegisterModuleAsync(new ModuleRegistration { Key = "receivables", Name = "Receivables", Enabled = true });
        SeededClient client = await fixture.SeedClientAsync(role: LedgerRole.Auditor);

        HttpResponseMessage resp = await client.Http.PostAsJsonAsync(
            $"/clients/{client.ClientId}/customers", new CreateCustomerRequest("Acme", "acme@example.com"));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Controller_creating_a_customer_over_http_succeeds()
    {
        ControlStore control = fixture.Control();
        await control.RegisterModuleAsync(new ModuleRegistration { Key = "receivables", Name = "Receivables", Enabled = true });
        SeededClient client = await fixture.SeedClientAsync(role: LedgerRole.Controller);

        HttpResponseMessage resp = await client.Http.PostAsJsonAsync(
            $"/clients/{client.ClientId}/customers", new CreateCustomerRequest("Acme", "acme@example.com"));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }
}
