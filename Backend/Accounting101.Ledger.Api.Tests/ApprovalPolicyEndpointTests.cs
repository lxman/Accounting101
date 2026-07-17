using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

public sealed class ApprovalPolicyEndpointTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private async Task<(Guid clientId, HttpClient http)> MemberWithAsync(params string[] caps)
    {
        SeededClient c = await fixture.SeedClientAsync("PolicyCaps");
        Guid userId = Guid.NewGuid();
        await fixture.Control().SetMembershipAsync(userId, c.ClientId, [], caps);
        return (c.ClientId, fixture.ClientFor(userId, "Member"));
    }

    [Fact]
    public async Task Holder_may_set_then_get_reflects_it()
    {
        (Guid clientId, HttpClient http) = await MemberWithAsync(Capabilities.AdminApprovalPolicy, Capabilities.GlRead);

        HttpResponseMessage put = await http.PutAsJsonAsync(
            $"/clients/{clientId}/approval-policy", new SetApprovalPolicyRequest(ApprovalMode.AutoApprove));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        ApprovalPolicyResponse got = (await (await http.GetAsync(
            $"/clients/{clientId}/approval-policy")).Content.ReadFromJsonAsync<ApprovalPolicyResponse>())!;
        Assert.Equal(ApprovalMode.AutoApprove, got.Mode);
    }

    [Fact]
    public async Task Member_without_cap_is_forbidden()
    {
        (Guid clientId, HttpClient http) = await MemberWithAsync(Capabilities.GlRead);
        HttpResponseMessage resp = await http.GetAsync($"/clients/{clientId}/approval-policy");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Deployment_admin_may_set()
    {
        SeededClient c = await fixture.SeedClientAsync("PolicyDeploy");
        HttpResponseMessage resp = await fixture.AdminClient().PutAsJsonAsync(
            $"/clients/{c.ClientId}/approval-policy", new SetApprovalPolicyRequest(ApprovalMode.SelfApprove));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Setting_unspecified_is_rejected()
    {
        (Guid clientId, HttpClient http) = await MemberWithAsync(Capabilities.AdminApprovalPolicy);
        HttpResponseMessage resp = await http.PutAsJsonAsync(
            $"/clients/{clientId}/approval-policy", new SetApprovalPolicyRequest(ApprovalMode.Unspecified));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }

    [Fact]
    public async Task Get_resolves_legacy_client_to_two_person()
    {
        SeededClient c = await fixture.SeedClientAsync("LegacySod", requireSod: true); // no ApprovalMode stored
        HttpResponseMessage resp = await fixture.AdminClient().GetAsync($"/clients/{c.ClientId}/approval-policy");
        ApprovalPolicyResponse got = (await resp.Content.ReadFromJsonAsync<ApprovalPolicyResponse>())!;
        Assert.Equal(ApprovalMode.TwoPerson, got.Mode);
    }

    private static PostEntryRequest Balanced(Guid? id, string date, Guid debit, Guid credit, decimal amount = 100m) =>
        new(id, DateOnly.Parse(date), null, null,
            [new PostLineRequest(debit, "Debit", amount), new PostLineRequest(credit, "Credit", amount)]);

    [Fact]
    public async Task Cannot_switch_to_auto_approve_while_entries_await_approval()
    {
        // SelfApprove leaves a post PendingApproval (see ApprovalModeEnforcementTests).
        SeededClient c = await fixture.SeedClientAsync("BlockAuto", approvalMode: ApprovalMode.SelfApprove);
        HttpResponseMessage post = await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/entries",
            Balanced(Guid.NewGuid(), "2026-03-31", Guid.NewGuid(), Guid.NewGuid()));
        post.EnsureSuccessStatusCode();

        HttpResponseMessage put = await fixture.AdminClient().PutAsJsonAsync(
            $"/clients/{c.ClientId}/approval-policy", new SetApprovalPolicyRequest(ApprovalMode.AutoApprove));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, put.StatusCode);

        // Mode not persisted; GET still reports SelfApprove and the pending count.
        ApprovalPolicyResponse got = (await fixture.AdminClient().GetFromJsonAsync<ApprovalPolicyResponse>(
            $"/clients/{c.ClientId}/approval-policy"))!;
        Assert.Equal(ApprovalMode.SelfApprove, got.Mode);
        Assert.Equal(1, got.PendingApprovalCount);
    }

    [Fact]
    public async Task Can_switch_to_auto_approve_when_nothing_is_pending()
    {
        SeededClient c = await fixture.SeedClientAsync("BlockAutoOk", approvalMode: ApprovalMode.SelfApprove);
        HttpResponseMessage put = await fixture.AdminClient().PutAsJsonAsync(
            $"/clients/{c.ClientId}/approval-policy", new SetApprovalPolicyRequest(ApprovalMode.AutoApprove));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
    }

    [Fact]
    public async Task Switching_to_two_person_is_not_blocked_by_pending_entries()
    {
        SeededClient c = await fixture.SeedClientAsync("PendingTwoPerson", approvalMode: ApprovalMode.SelfApprove);
        HttpResponseMessage post = await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/entries",
            Balanced(Guid.NewGuid(), "2026-03-31", Guid.NewGuid(), Guid.NewGuid()));
        post.EnsureSuccessStatusCode();

        HttpResponseMessage put = await fixture.AdminClient().PutAsJsonAsync(
            $"/clients/{c.ClientId}/approval-policy", new SetApprovalPolicyRequest(ApprovalMode.TwoPerson));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
    }
}
