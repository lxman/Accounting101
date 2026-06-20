using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>
/// Reversing an entry books a negating <c>Reversing</c> entry (pending until approved) and leaves the
/// original on the books — which is how a closed period gets corrected without unfreezing it.
/// </summary>
public sealed class ReverseTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private static PostEntryRequest Entry(DateOnly date, Guid debit, Guid credit, decimal amount) =>
        new(null, date, null, null,
            [new PostLineRequest(debit, "Debit", amount), new PostLineRequest(credit, "Credit", amount)]);

    private static async Task<Guid> PostAndApproveAsync(
        HttpClient http, Guid client, DateOnly date, Guid debit, Guid credit, decimal amount)
    {
        HttpResponseMessage posted = await http.PostAsJsonAsync($"/clients/{client}/entries", Entry(date, debit, credit, amount));
        posted.EnsureSuccessStatusCode();
        PostEntryResponse created = (await posted.Content.ReadFromJsonAsync<PostEntryResponse>())!;
        (await http.PostAsync($"/clients/{client}/entries/{created.Id}/approve", null)).EnsureSuccessStatusCode();
        return created.Id;
    }

    private async Task<AccountBalanceResponse> BalanceAsync(HttpClient http, Guid client, Guid account) =>
        (await http.GetFromJsonAsync<AccountBalanceResponse>($"/clients/{client}/accounts/{account}/balance"))!;

    [Fact]
    public async Task Reversing_an_entry_books_a_negating_entry_and_nets_to_zero()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid cash = Guid.NewGuid(), revenue = Guid.NewGuid();
        Guid originalId = await PostAndApproveAsync(c.Http, c.ClientId, new DateOnly(2026, 3, 31), cash, revenue, 100m);

        HttpResponseMessage reversed = await c.Http.PostAsJsonAsync(
            $"/clients/{c.ClientId}/entries/{originalId}/reverse",
            new ReverseRequest(new DateOnly(2026, 4, 1), "accrual reversal"));
        Assert.Equal(HttpStatusCode.Created, reversed.StatusCode);
        EntryResponse reversal = (await reversed.Content.ReadFromJsonAsync<EntryResponse>())!;
        Assert.Equal(originalId, reversal.ReversalOf);
        Assert.Equal("Reversing", reversal.Type);
        Assert.Equal("PendingApproval", reversal.Posting);

        // Pending: the books are unchanged until the reversal is approved.
        Assert.Equal(100m, (await BalanceAsync(c.Http, c.ClientId, cash)).Balance);

        (await c.Http.PostAsync($"/clients/{c.ClientId}/entries/{reversal.Id}/approve", null)).EnsureSuccessStatusCode();

        // Now it nets to zero, and the original remains on the books.
        Assert.Equal(0m, (await BalanceAsync(c.Http, c.ClientId, cash)).Balance);
        EntryResponse original = (await c.Http.GetFromJsonAsync<EntryResponse>($"/clients/{c.ClientId}/entries/{originalId}"))!;
        Assert.Equal("Active", original.Status);
        Assert.Equal("Posted", original.Posting);
    }

    [Fact]
    public async Task An_entry_cannot_be_reversed_twice_and_its_timeline_records_the_reversal()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid cash = Guid.NewGuid(), revenue = Guid.NewGuid();
        Guid id = await PostAndApproveAsync(c.Http, c.ClientId, new DateOnly(2026, 3, 31), cash, revenue, 100m);

        HttpResponseMessage first = await c.Http.PostAsJsonAsync(
            $"/clients/{c.ClientId}/entries/{id}/reverse", new ReverseRequest(new DateOnly(2026, 4, 1), "first"));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        // A second reversal of the same entry is refused — it would over-correct.
        HttpResponseMessage second = await c.Http.PostAsJsonAsync(
            $"/clients/{c.ClientId}/entries/{id}/reverse", new ReverseRequest(new DateOnly(2026, 4, 2), "second"));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        // The original's own audit timeline shows it was reversed (recorded without mutating the entry).
        AuditRecordResponse[] timeline = (await c.Http.GetFromJsonAsync<AuditRecordResponse[]>(
            $"/clients/{c.ClientId}/audit/{id}"))!;
        Assert.Contains(timeline, a => a.Action == "Reversed");
    }

    [Fact]
    public async Task A_closed_period_entry_is_reversed_in_an_open_period_not_unfrozen()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid cash = Guid.NewGuid(), revenue = Guid.NewGuid();
        Guid id = await PostAndApproveAsync(c.Http, c.ClientId, new DateOnly(2026, 3, 31), cash, revenue, 100m);

        // Close March.
        (await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/periods/close",
            new ClosePeriodRequest(new DateOnly(2026, 3, 31)))).EnsureSuccessStatusCode();

        // Reversing back INTO the closed period is refused...
        HttpResponseMessage intoClosed = await c.Http.PostAsJsonAsync(
            $"/clients/{c.ClientId}/entries/{id}/reverse", new ReverseRequest(new DateOnly(2026, 3, 15), null));
        Assert.Equal(HttpStatusCode.Conflict, intoClosed.StatusCode);

        // ...but reversing in the open period (April) is allowed, leaving the frozen entry untouched.
        HttpResponseMessage intoOpen = await c.Http.PostAsJsonAsync(
            $"/clients/{c.ClientId}/entries/{id}/reverse", new ReverseRequest(new DateOnly(2026, 4, 1), "correct prior period"));
        Assert.Equal(HttpStatusCode.Created, intoOpen.StatusCode);
        EntryResponse reversal = (await intoOpen.Content.ReadFromJsonAsync<EntryResponse>())!;
        (await c.Http.PostAsync($"/clients/{c.ClientId}/entries/{reversal.Id}/approve", null)).EnsureSuccessStatusCode();

        Assert.Equal(0m, (await BalanceAsync(c.Http, c.ClientId, cash)).Balance);
        EntryResponse original = (await c.Http.GetFromJsonAsync<EntryResponse>($"/clients/{c.ClientId}/entries/{id}"))!;
        Assert.Equal("Active", original.Status); // the closed entry is frozen and intact
    }
}
