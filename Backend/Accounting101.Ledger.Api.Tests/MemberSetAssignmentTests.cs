using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

public sealed class MemberSetAssignmentTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    // Create a deployment-wide custom set via the admin surface; return its id.
    private async Task<Guid> CreateSetAsync(params string[] caps)
    {
        HttpClient admin = fixture.AdminClient();
        CapabilitySetResponse created = (await (await admin.PostAsJsonAsync("/capability-sets",
            new CreateCapabilitySetRequest("Assignable " + Guid.NewGuid().ToString("N"), null, caps)))
            .Content.ReadFromJsonAsync<CapabilitySetResponse>())!;
        return created.Id;
    }

    [Fact]
    public async Task Assigning_sets_resolves_the_members_capabilities_from_them()
    {
        SeededClient c = await fixture.SeedClientAsync(role: LedgerRole.Admin);   // admin.users holder
        Guid setId = await CreateSetAsync(Capabilities.GlRead, Capabilities.ArWrite);

        Guid newUser = Guid.NewGuid();
        await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/members",
            new AddClientMemberRequest(newUser, ["ArClerk"], ["gl.read", "ar.read", "ar.write"]));

        HttpResponseMessage res = await c.Http.PutAsJsonAsync(
            $"/clients/{c.ClientId}/members/{newUser}/sets", new AssignSetsRequest([setId]));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        MembershipResponse body = (await res.Content.ReadFromJsonAsync<MembershipResponse>())!;
        Assert.True(new HashSet<string> { Capabilities.GlRead, Capabilities.ArWrite }.SetEquals(body.Capabilities));
    }

    [Fact]
    public async Task Assigning_an_unknown_set_is_422()
    {
        SeededClient c = await fixture.SeedClientAsync(role: LedgerRole.Admin);
        Guid newUser = Guid.NewGuid();
        await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/members",
            new AddClientMemberRequest(newUser, ["Auditor"], ["gl.read"]));

        HttpResponseMessage res = await c.Http.PutAsJsonAsync(
            $"/clients/{c.ClientId}/members/{newUser}/sets", new AssignSetsRequest([Guid.NewGuid()]));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
    }

    [Fact]
    public async Task Assigning_a_non_member_is_404()
    {
        SeededClient c = await fixture.SeedClientAsync(role: LedgerRole.Admin);
        Guid setId = await CreateSetAsync(Capabilities.GlRead);
        HttpResponseMessage res = await c.Http.PutAsJsonAsync(
            $"/clients/{c.ClientId}/members/{Guid.NewGuid()}/sets", new AssignSetsRequest([setId]));
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task A_non_admin_member_is_forbidden()
    {
        SeededClient c = await fixture.SeedClientAsync(role: LedgerRole.Clerk);   // no admin.users
        Guid setId = await CreateSetAsync(Capabilities.GlRead);
        HttpResponseMessage res = await c.Http.PutAsJsonAsync(
            $"/clients/{c.ClientId}/members/{c.UserId}/sets", new AssignSetsRequest([setId]));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Assigning_the_last_admin_a_non_admin_set_is_409()
    {
        SeededClient c = await fixture.SeedClientAsync(role: LedgerRole.Admin);   // sole admin.users holder
        Guid readOnly = await CreateSetAsync(Capabilities.GlRead, Capabilities.AuditRead);

        HttpResponseMessage res = await c.Http.PutAsJsonAsync(
            $"/clients/{c.ClientId}/members/{c.UserId}/sets", new AssignSetsRequest([readOnly]));
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task A_raw_capability_edit_clears_a_prior_set_assignment()
    {
        SeededClient c = await fixture.SeedClientAsync(role: LedgerRole.Admin);
        Guid setId = await CreateSetAsync(Capabilities.GlRead, Capabilities.ApWrite);

        Guid newUser = Guid.NewGuid();
        await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/members",
            new AddClientMemberRequest(newUser, ["Auditor"], ["gl.read"]));
        await c.Http.PutAsJsonAsync($"/clients/{c.ClientId}/members/{newUser}/sets", new AssignSetsRequest([setId]));

        // Switch back to a raw-cap grant — the set reference must be dropped so the raw caps take effect.
        // Roles is empty (the Slice D raw-cap shape); a non-empty role name would live-bind to that
        // role's built-in set (Resolve step 2) and mask the literal inline capabilities under test here.
        await c.Http.PutAsJsonAsync($"/clients/{c.ClientId}/members/{newUser}",
            new SetMemberRequest([], ["gl.read"]));

        Membership m = (await fixture.Control().GetMembershipAsync(newUser, c.ClientId))!;
        Assert.Empty(m.GrantedSetIds);
        Assert.True(new HashSet<string> { Capabilities.GlRead }.SetEquals(m.Capabilities));
    }
}
