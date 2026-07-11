using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>
/// <c>POST /clients/{clientId}/entries/batch</c>: post many journal entries as one atomic
/// business event. All-or-nothing (every entry validates and writes, or none do), a 500-entry
/// cap, and whole-batch idempotency (a caller-supplied id on every entry lets the exact same
/// batch be re-POSTed safely; a partial match is refused rather than guessed at).
/// </summary>
public sealed class PostBatchEndpointTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    // ---- helpers ---------------------------------------------------------------------------

    private static PostEntryRequest BalancedEntry(
        Guid? id, string date, Guid debit, Guid credit, decimal amount = 100m) =>
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

    private static PostEntryRequest UnbalancedEntry(
        Guid? id, string date, Guid debit, Guid credit) =>
        new(
            Id: id,
            EffectiveDate: DateOnly.Parse(date),
            Reference: null,
            Memo: null,
            Lines:
            [
                new PostLineRequest(debit, "Debit", 100m),
                new PostLineRequest(credit, "Credit", 50m),
            ]);

    private static Task<HttpResponseMessage> PostBatchAsync(HttpClient http, Guid clientId, PostBatchRequest request) =>
        http.PostAsJsonAsync($"/clients/{clientId}/entries/batch", request);

    /// <summary>Parse the <c>errors</c> map from a ValidationProblemDetails response body.</summary>
    private static async Task<Dictionary<string, string[]>> ReadErrorsAsync(HttpResponseMessage resp)
    {
        string json = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(json);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (!doc.RootElement.TryGetProperty("errors", out JsonElement errorsEl))
            return errors;
        foreach (JsonProperty prop in errorsEl.EnumerateObject())
        {
            List<string> messages = [];
            foreach (JsonElement msg in prop.Value.EnumerateArray())
                messages.Add(msg.GetString() ?? "");
            errors[prop.Name] = [.. messages];
        }
        return errors;
    }

    private static async Task<TrialBalanceResponse> GetTrialBalanceAsync(HttpClient http, Guid clientId)
    {
        HttpResponseMessage resp = await http.GetAsync($"/clients/{clientId}/trial-balance");
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<TrialBalanceResponse>())!;
    }

    // ---- tests -------------------------------------------------------------------------------

    [Fact]
    public async Task Batch_posts_all_entries_and_returns_them_in_order()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid debit1 = Guid.NewGuid(), credit1 = Guid.NewGuid();
        Guid debit2 = Guid.NewGuid(), credit2 = Guid.NewGuid();
        Guid id1 = Guid.NewGuid(), id2 = Guid.NewGuid();

        HttpResponseMessage resp = await PostBatchAsync(c.Http, c.ClientId, new PostBatchRequest(
        [
            BalancedEntry(id1, "2026-03-31", debit1, credit1),
            BalancedEntry(id2, "2026-03-31", debit2, credit2),
        ]));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        List<PostEntryResponse>? body = await resp.Content.ReadFromJsonAsync<List<PostEntryResponse>>();
        Assert.NotNull(body);
        Assert.Equal(2, body!.Count);
        Assert.Equal(id1, body[0].Id); // input order preserved
        Assert.Equal(id2, body[1].Id);
    }

    [Fact]
    public async Task Batch_with_one_unbalanced_entry_writes_none_and_returns_422_keyed_by_index()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid debit1 = Guid.NewGuid(), credit1 = Guid.NewGuid();
        Guid debit2 = Guid.NewGuid(), credit2 = Guid.NewGuid();

        HttpResponseMessage resp = await PostBatchAsync(c.Http, c.ClientId, new PostBatchRequest(
        [
            BalancedEntry(null, "2026-03-31", debit1, credit1),
            UnbalancedEntry(null, "2026-03-31", debit2, credit2),
        ]));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Dictionary<string, string[]> errors = await ReadErrorsAsync(resp);
        Assert.True(
            errors.Keys.Any(k => k.StartsWith("entries[1]", StringComparison.Ordinal)),
            $"Expected a key starting with 'entries[1]' in: {string.Join(", ", errors.Keys)}");
        Assert.False(
            errors.Keys.Any(k => k.StartsWith("entries[0]", StringComparison.Ordinal)),
            $"entries[0] was valid and should not have an error: {string.Join(", ", errors.Keys)}");

        // Neither entry landed — atomic all-or-nothing.
        TrialBalanceResponse tb = await GetTrialBalanceAsync(c.Http, c.ClientId);
        Assert.DoesNotContain(tb.Accounts, a => a.AccountId == debit1 || a.AccountId == credit1);
        Assert.DoesNotContain(tb.Accounts, a => a.AccountId == debit2 || a.AccountId == credit2);
    }

    [Fact]
    public async Task Batch_replay_all_ids_present_returns_200()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid debit1 = Guid.NewGuid(), credit1 = Guid.NewGuid();
        Guid debit2 = Guid.NewGuid(), credit2 = Guid.NewGuid();
        Guid id1 = Guid.NewGuid(), id2 = Guid.NewGuid();
        PostBatchRequest batch = new(
        [
            BalancedEntry(id1, "2026-03-31", debit1, credit1),
            BalancedEntry(id2, "2026-03-31", debit2, credit2),
        ]);

        HttpResponseMessage first = await PostBatchAsync(c.Http, c.ClientId, batch);
        first.EnsureSuccessStatusCode();

        HttpResponseMessage second = await PostBatchAsync(c.Http, c.ClientId, batch);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode); // whole-batch replay
        List<PostEntryResponse>? body = await second.Content.ReadFromJsonAsync<List<PostEntryResponse>>();
        Assert.NotNull(body);
        Assert.Equal(2, body!.Count);
        Assert.Equal(id1, body[0].Id);
        Assert.Equal(id2, body[1].Id);
    }

    [Fact]
    public async Task Batch_mixed_replay_returns_422()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid debit1 = Guid.NewGuid(), credit1 = Guid.NewGuid();
        Guid debit2 = Guid.NewGuid(), credit2 = Guid.NewGuid();
        Guid id1 = Guid.NewGuid(), id2 = Guid.NewGuid();

        // Post entry 1 alone first, so id1 already exists.
        HttpResponseMessage firstPost = await PostBatchAsync(c.Http, c.ClientId, new PostBatchRequest(
        [
            BalancedEntry(id1, "2026-03-31", debit1, credit1),
        ]));
        firstPost.EnsureSuccessStatusCode();

        // Re-submit as a batch alongside a brand-new id2 — a partial replay is ambiguous.
        HttpResponseMessage resp = await PostBatchAsync(c.Http, c.ClientId, new PostBatchRequest(
        [
            BalancedEntry(id1, "2026-03-31", debit1, credit1),
            BalancedEntry(id2, "2026-03-31", debit2, credit2),
        ]));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }

    [Fact]
    public async Task Batch_over_500_returns_422()
    {
        SeededClient c = await fixture.SeedClientAsync();
        List<PostEntryRequest> entries = [];
        for (int i = 0; i < 501; i++)
            entries.Add(BalancedEntry(null, "2026-03-31", Guid.NewGuid(), Guid.NewGuid()));

        HttpResponseMessage resp = await PostBatchAsync(c.Http, c.ClientId, new PostBatchRequest(entries));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }

    [Fact]
    public async Task Batch_empty_returns_422()
    {
        SeededClient c = await fixture.SeedClientAsync();

        HttpResponseMessage resp = await PostBatchAsync(c.Http, c.ClientId, new PostBatchRequest([]));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }
}
