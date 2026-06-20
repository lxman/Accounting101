using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Api.Contracts;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Mongo.Documents;
using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>
/// End-to-end proof of the host spine: an authenticated request authorizes against the control DB,
/// routes to the right client's ledger database, and lands the real authenticated actor in that
/// client's audit log — plus the negative paths (no token, no membership, unbalanced) and isolation.
/// </summary>
public sealed class SpineTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private static PostEntryRequest BalancedEntry(Guid debit, Guid credit, decimal amount) =>
        new(
            Id: null,
            EffectiveDate: new DateOnly(2026, 6, 30),
            Reference: "INV-1",
            Memo: "spine test",
            Lines:
            [
                new PostLineRequest(debit, "Debit", amount),
                new PostLineRequest(credit, "Credit", amount),
            ]);

    private async Task<(Guid Client, string Db)> RegisterClientAsync(ControlStore control, string name)
    {
        var client = Guid.NewGuid();
        string db = "client_" + client.ToString("N");
        await control.RegisterClientAsync(new ClientRegistration { Id = client, Name = name, DatabaseName = db });
        return (client, db);
    }

    [Fact]
    public async Task Authenticated_member_can_post_and_the_actor_lands_in_the_audit_log()
    {
        ControlStore control = fixture.Control();
        (Guid client, _) = await RegisterClientAsync(control, "Acme");
        var user = Guid.NewGuid();
        await control.AddMembershipAsync(user, client);

        HttpClient http = fixture.ClientFor(user, "Dana Clerk", ("role", "clerk"));

        HttpResponseMessage posted = await http.PostAsJsonAsync(
            $"/clients/{client}/entries", BalancedEntry(Guid.NewGuid(), Guid.NewGuid(), 250m));

        Assert.Equal(HttpStatusCode.Created, posted.StatusCode);
        var created = await posted.Content.ReadFromJsonAsync<PostEntryResponse>();
        Assert.NotNull(created);
        Assert.Equal("PendingApproval", created!.Posting);

        AuditRecordResponse[]? audit = await http.GetFromJsonAsync<AuditRecordResponse[]>(
            $"/clients/{client}/audit/{created.Id}");

        Assert.NotNull(audit);
        AuditRecordResponse record = Assert.Single(audit!);
        Assert.Equal("Created", record.Action);
        Assert.Equal(user, record.Actor.UserId);
        Assert.Equal("Dana Clerk", record.Actor.Name);
        Assert.Contains(record.Actor.Claims, c => c is { Type: "role", Value: "clerk" });
    }

    [Fact]
    public async Task Unauthenticated_request_is_rejected()
    {
        HttpClient http = fixture.AnonymousClient();
        HttpResponseMessage posted = await http.PostAsJsonAsync(
            $"/clients/{Guid.NewGuid()}/entries", BalancedEntry(Guid.NewGuid(), Guid.NewGuid(), 10m));
        Assert.Equal(HttpStatusCode.Unauthorized, posted.StatusCode);
    }

    [Fact]
    public async Task A_user_without_membership_is_forbidden()
    {
        ControlStore control = fixture.Control();
        (Guid clientA, _) = await RegisterClientAsync(control, "A");
        (Guid clientB, _) = await RegisterClientAsync(control, "B");
        var userA = Guid.NewGuid();
        await control.AddMembershipAsync(userA, clientA); // member of A only

        HttpClient http = fixture.ClientFor(userA, "A user", ("role", "clerk"));
        HttpResponseMessage posted = await http.PostAsJsonAsync(
            $"/clients/{clientB}/entries", BalancedEntry(Guid.NewGuid(), Guid.NewGuid(), 10m));

        Assert.Equal(HttpStatusCode.Forbidden, posted.StatusCode);
    }

    [Fact]
    public async Task An_unbalanced_entry_is_unprocessable()
    {
        ControlStore control = fixture.Control();
        (Guid client, _) = await RegisterClientAsync(control, "C");
        var user = Guid.NewGuid();
        await control.AddMembershipAsync(user, client);

        HttpClient http = fixture.ClientFor(user, "C user", ("role", "clerk"));
        PostEntryRequest unbalanced = new(
            Id: null, EffectiveDate: new DateOnly(2026, 6, 30), Reference: null, Memo: null,
            Lines: [new PostLineRequest(Guid.NewGuid(), "Debit", 100m), new PostLineRequest(Guid.NewGuid(), "Credit", 99m)]);

        HttpResponseMessage posted = await http.PostAsJsonAsync($"/clients/{client}/entries", unbalanced);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, posted.StatusCode);
    }

    [Fact]
    public async Task Posts_are_isolated_to_their_client_database()
    {
        ControlStore control = fixture.Control();
        (Guid clientA, string dbA) = await RegisterClientAsync(control, "A");
        (Guid clientB, string dbB) = await RegisterClientAsync(control, "B");
        var userA = Guid.NewGuid();
        await control.AddMembershipAsync(userA, clientA);

        HttpClient http = fixture.ClientFor(userA, "A user", ("role", "clerk"));
        HttpResponseMessage posted = await http.PostAsJsonAsync(
            $"/clients/{clientA}/entries", BalancedEntry(Guid.NewGuid(), Guid.NewGuid(), 500m));
        Assert.Equal(HttpStatusCode.Created, posted.StatusCode);

        long inA = await fixture.ClientDatabase(dbA)
            .GetCollection<JournalEntryDocument>("journal")
            .CountDocumentsAsync(FilterDefinition<JournalEntryDocument>.Empty);
        long inB = await fixture.ClientDatabase(dbB)
            .GetCollection<JournalEntryDocument>("journal")
            .CountDocumentsAsync(FilterDefinition<JournalEntryDocument>.Empty);

        Assert.Equal(1, inA);
        Assert.Equal(0, inB); // client B's database never saw it
    }
}
