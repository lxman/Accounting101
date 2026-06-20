using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Api.Contracts;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>
/// Drives the command + query surface end to end: approve puts an entry on the books and the trial
/// balance reflects it; void reverses it; revise supersedes and links; close snapshots and freezes;
/// the as-of trial balance excludes later entries; and the audit chain verifies.
/// </summary>
public sealed class CommandQueryTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private static PostEntryRequest Entry(long seq, DateOnly date, Guid debit, Guid credit, decimal amount) =>
        new(null, date, null, null, // seq is engine-assigned; the parameter just keeps call sites readable
            [new PostLineRequest(debit, "Debit", amount), new PostLineRequest(credit, "Credit", amount)]);

    private static async Task<Guid> PostAndApproveAsync(
        HttpClient http, Guid client, long seq, DateOnly date, Guid debit, Guid credit, decimal amount)
    {
        HttpResponseMessage posted = await http.PostAsJsonAsync($"/clients/{client}/entries", Entry(seq, date, debit, credit, amount));
        posted.EnsureSuccessStatusCode();
        PostEntryResponse created = (await posted.Content.ReadFromJsonAsync<PostEntryResponse>())!;

        HttpResponseMessage approved = await http.PostAsync($"/clients/{client}/entries/{created.Id}/approve", null);
        approved.EnsureSuccessStatusCode();
        return created.Id;
    }

    [Fact]
    public async Task Approve_puts_the_entry_on_the_books_and_the_trial_balance_reflects_it()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid cash = Guid.NewGuid(), revenue = Guid.NewGuid();

        await PostAndApproveAsync(c.Http, c.ClientId, 1, new DateOnly(2026, 3, 31), cash, revenue, 250m);

        TrialBalanceResponse tb = (await c.Http.GetFromJsonAsync<TrialBalanceResponse>($"/clients/{c.ClientId}/trial-balance"))!;
        Assert.Equal(250m, tb.Accounts.Single(a => a.AccountId == cash).Balance);
        Assert.Equal(-250m, tb.Accounts.Single(a => a.AccountId == revenue).Balance);

        AccountBalanceResponse bal = (await c.Http.GetFromJsonAsync<AccountBalanceResponse>(
            $"/clients/{c.ClientId}/accounts/{cash}/balance"))!;
        Assert.Equal(250m, bal.Balance);
    }

    [Fact]
    public async Task Void_reverses_an_approved_entry()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid cash = Guid.NewGuid(), revenue = Guid.NewGuid();
        Guid id = await PostAndApproveAsync(c.Http, c.ClientId, 1, new DateOnly(2026, 3, 31), cash, revenue, 100m);

        HttpResponseMessage voided = await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/entries/{id}/void", new VoidRequest("keyed twice"));
        Assert.Equal(HttpStatusCode.OK, voided.StatusCode);

        AccountBalanceResponse bal = (await c.Http.GetFromJsonAsync<AccountBalanceResponse>(
            $"/clients/{c.ClientId}/accounts/{cash}/balance"))!;
        Assert.Equal(0m, bal.Balance);

        EntryResponse entry = (await c.Http.GetFromJsonAsync<EntryResponse>($"/clients/{c.ClientId}/entries/{id}"))!;
        Assert.Equal("Voided", entry.Status);
    }

    [Fact]
    public async Task A_revision_has_no_effect_until_approved_then_swaps()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid cash = Guid.NewGuid(), revenue = Guid.NewGuid();
        Guid originalId = await PostAndApproveAsync(c.Http, c.ClientId, 1, new DateOnly(2026, 3, 31), cash, revenue, 100m);

        // Propose a correction (120). It is pending and must not move the books or the original.
        ReviseRequest revise = new(
            Id: null, EffectiveDate: new DateOnly(2026, 3, 31),
            Reference: null, Memo: null, Reason: "corrected amount",
            Lines: [new PostLineRequest(cash, "Debit", 120m), new PostLineRequest(revenue, "Credit", 120m)]);

        HttpResponseMessage revised = await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/entries/{originalId}/revise", revise);
        Assert.Equal(HttpStatusCode.Created, revised.StatusCode);
        EntryResponse replacement = (await revised.Content.ReadFromJsonAsync<EntryResponse>())!;
        Assert.Equal(originalId, replacement.Supersedes);
        Assert.Equal("PendingApproval", replacement.Posting);

        // No hole: the books still show the original, which stays active, until the revision is approved.
        AccountBalanceResponse beforeApprove = (await c.Http.GetFromJsonAsync<AccountBalanceResponse>(
            $"/clients/{c.ClientId}/accounts/{cash}/balance"))!;
        Assert.Equal(100m, beforeApprove.Balance);
        EntryResponse stillActive = (await c.Http.GetFromJsonAsync<EntryResponse>($"/clients/{c.ClientId}/entries/{originalId}"))!;
        Assert.Equal("Active", stillActive.Status);

        // Approving the revision swaps atomically: the replacement posts, the original is superseded.
        HttpResponseMessage approved = await c.Http.PostAsync($"/clients/{c.ClientId}/entries/{replacement.Id}/approve", null);
        approved.EnsureSuccessStatusCode();

        AccountBalanceResponse afterApprove = (await c.Http.GetFromJsonAsync<AccountBalanceResponse>(
            $"/clients/{c.ClientId}/accounts/{cash}/balance"))!;
        Assert.Equal(120m, afterApprove.Balance);
        EntryResponse original = (await c.Http.GetFromJsonAsync<EntryResponse>($"/clients/{c.ClientId}/entries/{originalId}"))!;
        Assert.Equal("Superseded", original.Status);
        Assert.Equal(replacement.Id, original.SupersededBy);
    }

    [Fact]
    public async Task Close_snapshots_balances_and_freezes_the_period()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid cash = Guid.NewGuid(), revenue = Guid.NewGuid();
        await PostAndApproveAsync(c.Http, c.ClientId, 1, new DateOnly(2026, 3, 31), cash, revenue, 100m);

        HttpResponseMessage closed = await c.Http.PostAsJsonAsync(
            $"/clients/{c.ClientId}/periods/close", new ClosePeriodRequest(new DateOnly(2026, 3, 31)));
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
        CloseResponse snapshot = (await closed.Content.ReadFromJsonAsync<CloseResponse>())!;
        Assert.Equal(100m, snapshot.OpeningBalances.Single(a => a.AccountId == cash).Balance);

        // Posting back into the now-closed period is rejected.
        HttpResponseMessage late = await c.Http.PostAsJsonAsync(
            $"/clients/{c.ClientId}/entries", Entry(2, new DateOnly(2026, 3, 15), cash, revenue, 10m));
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
    }

    [Fact]
    public async Task Trial_balance_as_of_excludes_later_entries()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid cash = Guid.NewGuid(), revenue = Guid.NewGuid();
        await PostAndApproveAsync(c.Http, c.ClientId, 1, new DateOnly(2026, 1, 31), cash, revenue, 100m);
        await PostAndApproveAsync(c.Http, c.ClientId, 2, new DateOnly(2026, 2, 28), cash, revenue, 50m);

        TrialBalanceResponse jan = (await c.Http.GetFromJsonAsync<TrialBalanceResponse>(
            $"/clients/{c.ClientId}/trial-balance?asOf=2026-01-31"))!;
        Assert.Equal(100m, jan.Accounts.Single(a => a.AccountId == cash).Balance);

        TrialBalanceResponse now = (await c.Http.GetFromJsonAsync<TrialBalanceResponse>(
            $"/clients/{c.ClientId}/trial-balance"))!;
        Assert.Equal(150m, now.Accounts.Single(a => a.AccountId == cash).Balance);
    }

    [Fact]
    public async Task Audit_verify_reports_a_valid_chain()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid cash = Guid.NewGuid(), revenue = Guid.NewGuid();
        await PostAndApproveAsync(c.Http, c.ClientId, 1, new DateOnly(2026, 3, 31), cash, revenue, 100m);

        AuditVerifyResponse verify = (await c.Http.GetFromJsonAsync<AuditVerifyResponse>(
            $"/clients/{c.ClientId}/audit/verify"))!;
        Assert.True(verify.Valid);
    }

    [Fact]
    public async Task Reading_an_entry_returns_its_line_detail()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid cash = Guid.NewGuid(), revenue = Guid.NewGuid();
        PostEntryRequest req = new(null, new DateOnly(2026, 3, 31), null, null,
            [new PostLineRequest(cash, "Debit", 250m), new PostLineRequest(revenue, "Credit", 250m)]);
        PostEntryResponse created = (await (await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/entries", req))
            .Content.ReadFromJsonAsync<PostEntryResponse>())!;

        EntryResponse read = (await c.Http.GetFromJsonAsync<EntryResponse>($"/clients/{c.ClientId}/entries/{created.Id}"))!;
        Assert.Equal(2, read.Lines.Count);
        EntryLineResponse debit = read.Lines.Single(l => l.AccountId == cash);
        Assert.Equal("Debit", debit.Direction);
        Assert.Equal(250m, debit.Amount);
    }

    [Fact]
    public async Task An_adjusting_entry_posts_but_a_closing_entry_is_refused()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();

        PostEntryRequest adjusting = new(null, new DateOnly(2026, 3, 31), null, null,
            [new PostLineRequest(a, "Debit", 10m), new PostLineRequest(b, "Credit", 10m)], EntryType: "Adjusting");
        PostEntryResponse created = (await (await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/entries", adjusting))
            .Content.ReadFromJsonAsync<PostEntryResponse>())!;
        EntryResponse read = (await c.Http.GetFromJsonAsync<EntryResponse>($"/clients/{c.ClientId}/entries/{created.Id}"))!;
        Assert.Equal("Adjusting", read.Type);

        // Closing is engine-generated only — the post endpoint refuses it.
        PostEntryRequest closing = new(null, new DateOnly(2026, 3, 31), null, null,
            [new PostLineRequest(a, "Debit", 10m), new PostLineRequest(b, "Credit", 10m)], EntryType: "Closing");
        HttpResponseMessage refused = await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/entries", closing);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
    }

    [Fact]
    public async Task The_entry_list_pages_with_skip_and_limit()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        for (int i = 0; i < 3; i++)
            await PostAndApproveAsync(c.Http, c.ClientId, i + 1, new DateOnly(2026, 3, 31), a, b, 10m);

        List<EntryResponse> firstTwo = (await c.Http.GetFromJsonAsync<List<EntryResponse>>(
            $"/clients/{c.ClientId}/entries?limit=2"))!;
        Assert.Equal(2, firstTwo.Count);

        List<EntryResponse> rest = (await c.Http.GetFromJsonAsync<List<EntryResponse>>(
            $"/clients/{c.ClientId}/entries?skip=2&limit=2"))!;
        Assert.Single(rest);
    }

    [Fact]
    public async Task Approving_a_missing_entry_is_not_found()
    {
        SeededClient c = await fixture.SeedClientAsync();
        HttpResponseMessage approved = await c.Http.PostAsync($"/clients/{c.ClientId}/entries/{Guid.NewGuid()}/approve", null);
        Assert.Equal(HttpStatusCode.NotFound, approved.StatusCode);
    }
}
