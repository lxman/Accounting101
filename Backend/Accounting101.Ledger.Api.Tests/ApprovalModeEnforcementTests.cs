using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

public sealed class ApprovalModeEnforcementTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private static PostEntryRequest Balanced(Guid? id, string date, Guid debit, Guid credit, decimal amount = 100m) =>
        new(id, DateOnly.Parse(date), null, null,
            [new PostLineRequest(debit, "Debit", amount), new PostLineRequest(credit, "Credit", amount)]);

    private static async Task<PostEntryResponse> PostAsync(HttpClient http, Guid clientId, PostEntryRequest req)
    {
        HttpResponseMessage resp = await http.PostAsJsonAsync($"/clients/{clientId}/entries", req);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<PostEntryResponse>())!;
    }

    [Fact]
    public async Task Auto_approve_lands_a_single_post_as_posted()
    {
        SeededClient c = await fixture.SeedClientAsync("AutoSingle", approvalMode: ApprovalMode.AutoApprove);
        PostEntryResponse body = await PostAsync(c.Http, c.ClientId, Balanced(Guid.NewGuid(), "2026-03-31", Guid.NewGuid(), Guid.NewGuid()));
        Assert.Equal("Posted", body.Posting);
    }

    [Fact]
    public async Task Auto_approve_lands_a_batch_as_posted()
    {
        SeededClient c = await fixture.SeedClientAsync("AutoBatch", approvalMode: ApprovalMode.AutoApprove);
        HttpResponseMessage resp = await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/entries/batch",
            new PostBatchRequest([
                Balanced(Guid.NewGuid(), "2026-03-31", Guid.NewGuid(), Guid.NewGuid()),
                Balanced(Guid.NewGuid(), "2026-03-31", Guid.NewGuid(), Guid.NewGuid()),
            ]));
        resp.EnsureSuccessStatusCode();
        List<PostEntryResponse> body = (await resp.Content.ReadFromJsonAsync<List<PostEntryResponse>>())!;
        Assert.All(body, e => Assert.Equal("Posted", e.Posting));
    }

    [Fact]
    public async Task Self_approve_leaves_a_post_pending_until_approved()
    {
        SeededClient c = await fixture.SeedClientAsync("SelfMode", approvalMode: ApprovalMode.SelfApprove);
        PostEntryResponse body = await PostAsync(c.Http, c.ClientId, Balanced(Guid.NewGuid(), "2026-03-31", Guid.NewGuid(), Guid.NewGuid()));
        Assert.Equal("PendingApproval", body.Posting);
    }

    [Fact]
    public async Task Self_approve_lets_the_author_approve_their_own_entry()
    {
        SeededClient c = await fixture.SeedClientAsync("SelfApprove", approvalMode: ApprovalMode.SelfApprove);
        PostEntryResponse posted = await PostAsync(c.Http, c.ClientId, Balanced(Guid.NewGuid(), "2026-03-31", Guid.NewGuid(), Guid.NewGuid()));
        HttpResponseMessage approve = await c.Http.PostAsync($"/clients/{c.ClientId}/entries/{posted.Id}/approve", null);
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
    }

    [Fact]
    public async Task Two_person_forbids_the_author_approving_their_own_entry()
    {
        SeededClient c = await fixture.SeedClientAsync("TwoPerson", approvalMode: ApprovalMode.TwoPerson);
        PostEntryResponse posted = await PostAsync(c.Http, c.ClientId, Balanced(Guid.NewGuid(), "2026-03-31", Guid.NewGuid(), Guid.NewGuid()));
        HttpResponseMessage approve = await c.Http.PostAsync($"/clients/{c.ClientId}/entries/{posted.Id}/approve", null);
        Assert.Equal(HttpStatusCode.Forbidden, approve.StatusCode);
    }

    [Fact]
    public async Task Two_person_allows_a_different_approver()
    {
        SeededClient c = await fixture.SeedClientAsync("TwoPersonOk", approvalMode: ApprovalMode.TwoPerson);
        PostEntryResponse posted = await PostAsync(c.Http, c.ClientId, Balanced(Guid.NewGuid(), "2026-03-31", Guid.NewGuid(), Guid.NewGuid()));
        HttpClient approver = await fixture.AddMemberAsync(c.ClientId, Accounting101.Ledger.Api.Control.LedgerRole.Approver);
        HttpResponseMessage approve = await approver.PostAsync($"/clients/{c.ClientId}/entries/{posted.Id}/approve", null);
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
    }
}
