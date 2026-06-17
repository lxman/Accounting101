using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Api.Contracts;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>
/// Segregation of duties, enforced in the host (per client, opt-in): the person who enters an entry
/// cannot approve it. Because a revision is approved through the same endpoint, the rule covers
/// corrections too — a reviser cannot approve their own revision.
/// </summary>
public sealed class PolicyTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private static PostEntryRequest Entry(Guid debit, Guid credit, decimal amount) =>
        new(null, 1, new DateOnly(2026, 3, 31), null, null,
            [new PostLineRequest(debit, "Debit", amount), new PostLineRequest(credit, "Credit", amount)]);

    private static async Task<Guid> PostAsync(HttpClient http, Guid client, Guid debit, Guid credit, decimal amount)
    {
        HttpResponseMessage posted = await http.PostAsJsonAsync($"/clients/{client}/entries", Entry(debit, credit, amount));
        posted.EnsureSuccessStatusCode();
        return (await posted.Content.ReadFromJsonAsync<PostEntryResponse>())!.Id;
    }

    [Fact]
    public async Task With_sod_the_author_cannot_approve_their_own_entry()
    {
        SeededClient c = await fixture.SeedClientAsync(requireSod: true);
        Guid cash = Guid.NewGuid(), revenue = Guid.NewGuid();
        Guid id = await PostAsync(c.Http, c.ClientId, cash, revenue, 100m);

        HttpResponseMessage selfApprove = await c.Http.PostAsync($"/clients/{c.ClientId}/entries/{id}/approve", null);
        Assert.Equal(HttpStatusCode.Forbidden, selfApprove.StatusCode);

        HttpClient checker = await fixture.AddMemberAsync(c.ClientId, "Checker");
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
        HttpClient checker = await fixture.AddMemberAsync(c.ClientId, "Checker");
        (await checker.PostAsync($"/clients/{c.ClientId}/entries/{originalId}/approve", null)).EnsureSuccessStatusCode();

        // The checker proposes a correction...
        ReviseRequest revise = new(null, 2, new DateOnly(2026, 3, 31), null, null, "corrected amount",
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
}
