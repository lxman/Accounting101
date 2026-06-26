using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;
using Accounting101.Ledger.Api.Control;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>
/// Host policy: role-based permissions (what a role may do) and segregation of duties (an individual
/// may not approve their own entry). The two are orthogonal — roles gate capability, SoD gates the
/// same-person case — and both are enforced upstream of the engine.
/// </summary>
public sealed class PolicyTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private static PostEntryRequest Entry(Guid debit, Guid credit, decimal amount) =>
        new(null, new DateOnly(2026, 3, 31), null, null,
            [new PostLineRequest(debit, "Debit", amount), new PostLineRequest(credit, "Credit", amount)]);

    private static async Task<Guid> PostAsync(HttpClient http, Guid client, Guid debit, Guid credit, decimal amount)
    {
        HttpResponseMessage posted = await http.PostAsJsonAsync($"/clients/{client}/entries", Entry(debit, credit, amount));
        posted.EnsureSuccessStatusCode();
        return (await posted.Content.ReadFromJsonAsync<PostEntryResponse>())!.Id;
    }

    // ---- Segregation of duties --------------------------------------------------------------

    [Fact]
    public async Task With_sod_the_author_cannot_approve_their_own_entry()
    {
        SeededClient c = await fixture.SeedClientAsync(requireSod: true);
        Guid cash = Guid.NewGuid(), revenue = Guid.NewGuid();
        Guid id = await PostAsync(c.Http, c.ClientId, cash, revenue, 100m);

        HttpResponseMessage selfApprove = await c.Http.PostAsync($"/clients/{c.ClientId}/entries/{id}/approve", null);
        Assert.Equal(HttpStatusCode.Forbidden, selfApprove.StatusCode);

        HttpClient checker = await fixture.AddMemberAsync(c.ClientId);
        HttpResponseMessage otherApprove = await checker.PostAsync($"/clients/{c.ClientId}/entries/{id}/approve", null);
        Assert.Equal(HttpStatusCode.OK, otherApprove.StatusCode);
    }

    [Fact]
    public async Task Without_sod_the_author_can_self_approve()
    {
        SeededClient c = await fixture.SeedClientAsync(); // SoD off by default
        Guid cash = Guid.NewGuid(), revenue = Guid.NewGuid();
        Guid id = await PostAsync(c.Http, c.ClientId, cash, revenue, 100m);

        HttpResponseMessage selfApprove = await c.Http.PostAsync($"/clients/{c.ClientId}/entries/{id}/approve", null);
        Assert.Equal(HttpStatusCode.OK, selfApprove.StatusCode);
    }

    [Fact]
    public async Task With_sod_the_reviser_cannot_approve_their_own_revision()
    {
        SeededClient c = await fixture.SeedClientAsync(requireSod: true);
        Guid cash = Guid.NewGuid(), revenue = Guid.NewGuid();

        // Maker posts; a checker approves the original onto the books.
        Guid originalId = await PostAsync(c.Http, c.ClientId, cash, revenue, 100m);
        HttpClient checker = await fixture.AddMemberAsync(c.ClientId);
        (await checker.PostAsync($"/clients/{c.ClientId}/entries/{originalId}/approve", null)).EnsureSuccessStatusCode();

        // The checker proposes a correction...
        ReviseRequest revise = new(null, new DateOnly(2026, 3, 31), null, null, "corrected amount",
            [new PostLineRequest(cash, "Debit", 120m), new PostLineRequest(revenue, "Credit", 120m)]);
        HttpResponseMessage revised = await checker.PostAsJsonAsync($"/clients/{c.ClientId}/entries/{originalId}/revise", revise);
        revised.EnsureSuccessStatusCode();
        EntryResponse replacement = (await revised.Content.ReadFromJsonAsync<EntryResponse>())!;

        // ...and cannot approve their own revision, but the original's author (a different person) can.
        HttpResponseMessage selfApprove = await checker.PostAsync($"/clients/{c.ClientId}/entries/{replacement.Id}/approve", null);
        Assert.Equal(HttpStatusCode.Forbidden, selfApprove.StatusCode);

        HttpResponseMessage otherApprove = await c.Http.PostAsync($"/clients/{c.ClientId}/entries/{replacement.Id}/approve", null);
        Assert.Equal(HttpStatusCode.OK, otherApprove.StatusCode);
    }

    // ---- Role permissions -------------------------------------------------------------------

    [Fact]
    public async Task A_clerk_cannot_post_or_revise_raw_entries()
    {
        SeededClient c = await fixture.SeedClientAsync(role: LedgerRole.Clerk);
        Guid cash = Guid.NewGuid(), revenue = Guid.NewGuid();

        // Raw post is denied — a clerk writes only through modules now.
        HttpResponseMessage post = await c.Http.PostAsJsonAsync(
            $"/clients/{c.ClientId}/entries", Entry(cash, revenue, 100m));
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);

        // Raw revise is denied too (the permission check precedes the entry lookup, so a
        // nonexistent id still yields 403, not 404).
        HttpResponseMessage revise = await c.Http.PostAsJsonAsync(
            $"/clients/{c.ClientId}/entries/{Guid.NewGuid()}/revise",
            new ReviseRequest(null, new DateOnly(2026, 4, 1), null, null, "x",
                [new PostLineRequest(cash, "Debit", 100m), new PostLineRequest(revenue, "Credit", 100m)]));
        Assert.Equal(HttpStatusCode.Forbidden, revise.StatusCode);
    }

    [Fact]
    public async Task An_approver_can_approve_but_cannot_post()
    {
        SeededClient c = await fixture.SeedClientAsync(role: LedgerRole.Approver);
        Guid cash = Guid.NewGuid(), revenue = Guid.NewGuid();

        HttpResponseMessage post = await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/entries", Entry(cash, revenue, 100m));
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);

        // A controller posts (clerks no longer post raw); the approver approves.
        HttpClient poster = await fixture.AddMemberAsync(c.ClientId, LedgerRole.Controller);
        Guid id = await PostAsync(poster, c.ClientId, cash, revenue, 100m);
        HttpResponseMessage approve = await c.Http.PostAsync($"/clients/{c.ClientId}/entries/{id}/approve", null);
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
    }

    [Fact]
    public async Task An_auditor_can_read_but_not_write()
    {
        SeededClient c = await fixture.SeedClientAsync(role: LedgerRole.Auditor);

        HttpResponseMessage read = await c.Http.GetAsync($"/clients/{c.ClientId}/trial-balance");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        HttpResponseMessage post = await c.Http.PostAsJsonAsync(
            $"/clients/{c.ClientId}/entries", Entry(Guid.NewGuid(), Guid.NewGuid(), 100m));
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
    }

    [Fact]
    public async Task A_clerk_cannot_reverse()
    {
        SeededClient c = await fixture.SeedClientAsync(role: LedgerRole.Clerk);

        // The permission check precedes the entry lookup, so a clerk is refused outright.
        HttpResponseMessage reverse = await c.Http.PostAsJsonAsync(
            $"/clients/{c.ClientId}/entries/{Guid.NewGuid()}/reverse",
            new ReverseRequest(new DateOnly(2026, 4, 1), null));
        Assert.Equal(HttpStatusCode.Forbidden, reverse.StatusCode);
    }
}
