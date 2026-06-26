using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>
/// <c>POST /clients/{clientId}/entries</c> idempotency: a caller-supplied <c>Id</c> turns the
/// second identical POST into a replay (200 + existing entry) rather than a duplicate-key error.
/// The three decision branches are:
/// <list type="bullet">
///   <item>same id, same financial content → 200 (replay).</item>
///   <item>same id, different content → 422 (conflict-on-intent).</item>
///   <item>same id, different client OR no entry found → 409 (hard conflict, no leak).</item>
/// </list>
/// The no-id path is explicitly verified to remain opt-out (no dedup without an id).
/// </summary>
public sealed class IdempotentPostTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    // ---- helpers -------------------------------------------------------------------------------

    /// <summary>A balanced entry request with an explicit caller-supplied id.</summary>
    private static PostEntryRequest BalancedEntry(
        Guid id, string date, Guid debit, Guid credit, decimal amount = 100m) =>
        new(
            Id: id,
            EffectiveDate: DateOnly.Parse(date),
            Reference: null,
            Memo: null,
            Lines:
            [
                new PostLineRequest(debit, "Debit", amount),
                new PostLineRequest(credit, "Credit", amount),
            ]);

    /// <summary>A balanced entry request without a caller-supplied id (opt-out path).</summary>
    private static PostEntryRequest BalancedEntryNoId(
        string date, Guid debit, Guid credit, decimal amount = 100m) =>
        new(
            Id: null,
            EffectiveDate: DateOnly.Parse(date),
            Reference: null,
            Memo: null,
            Lines:
            [
                new PostLineRequest(debit, "Debit", amount),
                new PostLineRequest(credit, "Credit", amount),
            ]);

    /// <summary>Count of journal entries visible for this client.</summary>
    private static async Task<int> CountEntries(HttpClient http, Guid clientId)
    {
        HttpResponseMessage resp = await http.GetAsync($"/clients/{clientId}/entries");
        resp.EnsureSuccessStatusCode();
        List<EntryResponse>? entries = await resp.Content.ReadFromJsonAsync<List<EntryResponse>>();
        return entries?.Count ?? 0;
    }

    // ---- tests ---------------------------------------------------------------------------------

    [Fact]
    public async Task Reposting_the_same_id_returns_the_existing_entry_and_creates_nothing()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid debit = Guid.NewGuid(), credit = Guid.NewGuid();
        Guid id = Guid.NewGuid();
        PostEntryRequest body = BalancedEntry(id, "2024-06-30", debit, credit);

        HttpResponseMessage first = await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/entries", body);
        HttpResponseMessage second = await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/entries", body);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode); // idempotent replay
        Assert.Equal(id, (await second.Content.ReadFromJsonAsync<PostEntryResponse>())!.Id);
        Assert.Equal(1, await CountEntries(c.Http, c.ClientId)); // exactly one entry on the books
    }

    [Fact]
    public async Task Same_id_with_different_content_returns_422()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid debit = Guid.NewGuid(), credit = Guid.NewGuid();
        Guid id = Guid.NewGuid();

        (await c.Http.PostAsJsonAsync(
            $"/clients/{c.ClientId}/entries", BalancedEntry(id, "2024-06-30", debit, credit, amount: 100m)))
            .EnsureSuccessStatusCode();

        HttpResponseMessage clash = await c.Http.PostAsJsonAsync(
            $"/clients/{c.ClientId}/entries", BalancedEntry(id, "2024-06-30", debit, credit, amount: 200m));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, clash.StatusCode);
    }

    [Fact]
    public async Task Repost_after_approval_returns_the_posted_entry_no_duplicate()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid debit = Guid.NewGuid(), credit = Guid.NewGuid();
        Guid id = Guid.NewGuid();
        PostEntryRequest body = BalancedEntry(id, "2024-06-30", debit, credit);

        // Post and approve.
        HttpResponseMessage posted = await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/entries", body);
        posted.EnsureSuccessStatusCode();
        (await c.Http.PostAsync($"/clients/{c.ClientId}/entries/{id}/approve", null)).EnsureSuccessStatusCode();

        // Re-post the same body after approval.
        HttpResponseMessage second = await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/entries", body);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        PostEntryResponse replay = (await second.Content.ReadFromJsonAsync<PostEntryResponse>())!;
        Assert.Equal(id, replay.Id);
        Assert.Equal("Posted", replay.Posting);    // lifecycle state is what it actually is now
        Assert.Equal(1, await CountEntries(c.Http, c.ClientId)); // still exactly one entry
    }

    [Fact]
    public async Task Repost_after_period_close_returns_existing_not_409()
    {
        // Post with an explicit id while the period is open.
        SeededClient c = await fixture.SeedClientAsync();
        Guid debit = Guid.NewGuid(), credit = Guid.NewGuid();
        Guid id = Guid.NewGuid();
        PostEntryRequest body = BalancedEntry(id, "2024-06-30", debit, credit);

        (await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/entries", body)).EnsureSuccessStatusCode();
        (await c.Http.PostAsync($"/clients/{c.ClientId}/entries/{id}/approve", null)).EnsureSuccessStatusCode();

        // Close the period through the entry's effective date.
        (await c.Http.PostAsJsonAsync(
            $"/clients/{c.ClientId}/periods/close",
            new ClosePeriodRequest(new DateOnly(2024, 6, 30))))
            .EnsureSuccessStatusCode();

        // Re-post the same body with the same id in a now-closed period.
        // The freeze normally returns 409, but an idempotent replay must return 200 — the entry
        // already exists, so no new write is attempted and the freeze check never fires.
        HttpResponseMessage second = await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/entries", body);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode); // NOT a closed-period 409
        Assert.Equal(id, (await second.Content.ReadFromJsonAsync<PostEntryResponse>())!.Id);
        Assert.Equal(1, await CountEntries(c.Http, c.ClientId)); // still one entry
    }

    [Fact]
    public async Task Posting_without_an_id_twice_creates_two_entries()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid debit = Guid.NewGuid(), credit = Guid.NewGuid();
        PostEntryRequest body = BalancedEntryNoId("2024-06-30", debit, credit);

        HttpResponseMessage first = await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/entries", body);
        HttpResponseMessage second = await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/entries", body);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);  // first post must succeed
        Assert.Equal(HttpStatusCode.Created, second.StatusCode); // second post also Created (no dedup without an id)
        Assert.Equal(2, await CountEntries(c.Http, c.ClientId)); // opt-out preserved: no dedup without an id
    }

    [Fact]
    public async Task A_clients_id_is_never_resolved_to_another_clients_entry()
    {
        // Client A posts entry with id X.
        SeededClient a = await fixture.SeedClientAsync("ClientA");
        Guid debit = Guid.NewGuid(), credit = Guid.NewGuid();
        Guid sharedId = Guid.NewGuid();

        (await a.Http.PostAsJsonAsync(
            $"/clients/{a.ClientId}/entries", BalancedEntry(sharedId, "2024-06-30", debit, credit)))
            .EnsureSuccessStatusCode();

        // Client B (different client, different auth) re-posts the same body with the same id X.
        // Each client lives in its own database (per-tenant isolation), so the insert does not
        // collide in B's store and B gets its own entry (201). What MUST NOT happen is that a
        // second POST from B (which now DOES collide within B's DB) returns A's entry.
        SeededClient b = await fixture.SeedClientAsync("ClientB");
        (await b.Http.PostAsJsonAsync(
            $"/clients/{b.ClientId}/entries", BalancedEntry(sharedId, "2024-06-30", debit, credit)))
            .EnsureSuccessStatusCode(); // B owns this id in its own store — 201

        // Second post from B with the same id replays B's OWN entry (not A's), proving the
        // GetEntryAsync scoping (entry.ClientId == clientId) is correct.
        HttpResponseMessage replay = await b.Http.PostAsJsonAsync(
            $"/clients/{b.ClientId}/entries", BalancedEntry(sharedId, "2024-06-30", debit, credit));

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode); // idempotent replay within B's scope
        PostEntryResponse replayed = (await replay.Content.ReadFromJsonAsync<PostEntryResponse>())!;
        Assert.Equal(sharedId, replayed.Id); // B gets back its own entry id

        // Isolation: A and B each have exactly one entry; neither sees the other's data.
        Assert.Equal(1, await CountEntries(a.Http, a.ClientId));
        Assert.Equal(1, await CountEntries(b.Http, b.ClientId));
    }
}
