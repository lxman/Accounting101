using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>
/// Verifies the new <c>posting</c> and <c>reference</c> query parameters on
/// <c>GET /clients/{clientId}/entries</c>:
/// - <c>posting=PendingApproval</c>/<c>posting=Posted</c> filter by approval state (DB-level or in-memory);
/// - an invalid <c>posting</c> value returns 400 — never silently ignored;
/// - <c>reference</c> returns only entries with that exact reference string;
/// - an <c>absent</c> reference returns <c>[]</c>, not the full list (regression-proof);
/// - <c>account</c> and <c>posting</c> compose correctly.
/// </summary>
public sealed class EntriesListFilterTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    // ---- helpers ------------------------------------------------------------

    private static PostEntryRequest EntryRequest(DateOnly date, string? reference, params PostLineRequest[] lines) =>
        new(null, date, reference, null, lines);

    private static async Task<Guid> PostEntryAsync(HttpClient http, Guid clientId, DateOnly date,
        string? reference, Guid debit, Guid credit, decimal amount)
    {
        HttpResponseMessage resp = await http.PostAsJsonAsync(
            $"/clients/{clientId}/entries",
            EntryRequest(date, reference,
                new PostLineRequest(debit, "Debit", amount),
                new PostLineRequest(credit, "Credit", amount)));
        resp.EnsureSuccessStatusCode();
        PostEntryResponse created = (await resp.Content.ReadFromJsonAsync<PostEntryResponse>())!;
        return created.Id;
    }

    private static async Task ApproveAsync(HttpClient http, Guid clientId, Guid entryId)
    {
        HttpResponseMessage resp = await http.PostAsync(
            $"/clients/{clientId}/entries/{entryId}/approve", null);
        resp.EnsureSuccessStatusCode();
    }

    private static async Task<List<EntryResponse>> ListEntriesAsync(HttpClient http, Guid clientId, string query = "")
    {
        string url = $"/clients/{clientId}/entries" + (string.IsNullOrEmpty(query) ? "" : "?" + query);
        return (await http.GetFromJsonAsync<List<EntryResponse>>(url))!;
    }

    // ---- tests --------------------------------------------------------------

    [Fact]
    public async Task Posting_pending_returns_only_unapproved()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid debit = Guid.NewGuid(), credit = Guid.NewGuid();
        DateOnly date = new(2026, 3, 31);

        // Post two entries; approve one only.
        Guid pending = await PostEntryAsync(c.Http, c.ClientId, date, null, debit, credit, 100m);
        Guid approved = await PostEntryAsync(c.Http, c.ClientId, date, null, debit, credit, 200m);
        await ApproveAsync(c.Http, c.ClientId, approved);

        List<EntryResponse> entries = await ListEntriesAsync(c.Http, c.ClientId, "posting=PendingApproval");

        Assert.All(entries, e => Assert.Equal("PendingApproval", e.Posting));
        Assert.Contains(entries, e => e.Id == pending);
        Assert.DoesNotContain(entries, e => e.Id == approved);
    }

    [Fact]
    public async Task Posting_posted_returns_only_posted()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid debit = Guid.NewGuid(), credit = Guid.NewGuid();
        DateOnly date = new(2026, 3, 31);

        Guid pending = await PostEntryAsync(c.Http, c.ClientId, date, null, debit, credit, 100m);
        Guid approved = await PostEntryAsync(c.Http, c.ClientId, date, null, debit, credit, 200m);
        await ApproveAsync(c.Http, c.ClientId, approved);

        List<EntryResponse> entries = await ListEntriesAsync(c.Http, c.ClientId, "posting=Posted");

        Assert.All(entries, e => Assert.Equal("Posted", e.Posting));
        Assert.Contains(entries, e => e.Id == approved);
        Assert.DoesNotContain(entries, e => e.Id == pending);
    }

    [Fact]
    public async Task Invalid_posting_value_returns_400()
    {
        SeededClient c = await fixture.SeedClientAsync();

        HttpResponseMessage resp = await c.Http.GetAsync($"/clients/{c.ClientId}/entries?posting=Nope");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Reference_filter_returns_only_matching()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid debit = Guid.NewGuid(), credit = Guid.NewGuid();
        DateOnly date = new(2026, 3, 31);
        const string refTag = "INV-9001";

        Guid withRef = await PostEntryAsync(c.Http, c.ClientId, date, refTag, debit, credit, 100m);
        Guid noRef = await PostEntryAsync(c.Http, c.ClientId, date, null, debit, credit, 200m);

        List<EntryResponse> entries = await ListEntriesAsync(c.Http, c.ClientId, $"reference={refTag}");

        Assert.Contains(entries, e => e.Id == withRef);
        Assert.DoesNotContain(entries, e => e.Id == noRef);
    }

    [Fact]
    public async Task Absent_reference_returns_empty_not_all()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid debit = Guid.NewGuid(), credit = Guid.NewGuid();
        DateOnly date = new(2026, 3, 31);

        // Seed some entries so we can confirm the filter doesn't silently return all.
        await PostEntryAsync(c.Http, c.ClientId, date, null, debit, credit, 100m);
        await PostEntryAsync(c.Http, c.ClientId, date, "SOME-REF", debit, credit, 200m);

        List<EntryResponse> entries = await ListEntriesAsync(c.Http, c.ClientId, "reference=DOESNOTEXIST");

        Assert.Empty(entries);
    }

    [Fact]
    public async Task Account_and_posting_compose()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid cash = Guid.NewGuid(), revenue = Guid.NewGuid(), other = Guid.NewGuid();
        DateOnly date = new(2026, 3, 31);

        // Entry touching cash (pending — not approved).
        Guid pendingCash = await PostEntryAsync(c.Http, c.ClientId, date, null, cash, revenue, 100m);
        // Entry touching cash (approved).
        Guid approvedCash = await PostEntryAsync(c.Http, c.ClientId, date, null, cash, revenue, 200m);
        await ApproveAsync(c.Http, c.ClientId, approvedCash);
        // Entry NOT touching cash (pending).
        Guid pendingOther = await PostEntryAsync(c.Http, c.ClientId, date, null, other, revenue, 50m);

        List<EntryResponse> entries = await ListEntriesAsync(
            c.Http, c.ClientId, $"account={cash}&posting=PendingApproval");

        // Only pendingCash should appear: touches cash AND is pending.
        Assert.Contains(entries, e => e.Id == pendingCash);
        Assert.DoesNotContain(entries, e => e.Id == approvedCash);
        Assert.DoesNotContain(entries, e => e.Id == pendingOther);
    }
}
