using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

public sealed class GrantScopeTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    // A member holding admin.users (added below) so it can manage members, but NOT gl.post.
    // Note: no role name is granted alongside these raw capabilities — every LedgerRole has a
    // same-named built-in capability set, and Membership resolution live-binds a granted role to
    // that built-in set (overriding any inline capabilities), so admin.users would be silently
    // dropped if a role were specified here too.
    private async Task<SeededClient> SeedUserAdminClerkAsync()
    {
        SeededClient admin = await fixture.SeedClientAsync(role: LedgerRole.Admin);
        // Add a second member who can manage users (admin.users) but is otherwise a narrow AR clerk.
        Guid clerk = Guid.NewGuid();
        await admin.Http.PostAsJsonAsync($"/clients/{admin.ClientId}/members",
            new AddClientMemberRequest(clerk, [], ["gl.read", "ar.read", "ar.write", "admin.users"]));
        HttpClient clerkHttp = fixture.ClientFor(clerk, "User-Admin Clerk");
        return new SeededClient(admin.ClientId, admin.Database, clerk, clerkHttp);
    }

    [Fact]
    public async Task Raw_cap_grant_beyond_caller_scope_is_422()
    {
        SeededClient clerk = await SeedUserAdminClerkAsync();
        // The clerk lacks gl.post, so it cannot grant gl.post to anyone.
        HttpResponseMessage res = await clerk.Http.PostAsJsonAsync($"/clients/{clerk.ClientId}/members",
            new AddClientMemberRequest(Guid.NewGuid(), ["Controller"], ["gl.read", "gl.post"]));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
    }

    [Fact]
    public async Task Set_assignment_beyond_caller_scope_is_422()
    {
        SeededClient clerk = await SeedUserAdminClerkAsync();
        // Assign the built-in Controller set (has gl.post, which the clerk lacks) to a new member.
        Guid target = Guid.NewGuid();
        await clerk.Http.PostAsJsonAsync($"/clients/{clerk.ClientId}/members",
            new AddClientMemberRequest(target, ["ArClerk"], ["gl.read", "ar.read", "ar.write"]));
        CapabilitySet controller = (await fixture.Control().GetCapabilitySetByNameAsync("Controller"))!;
        HttpResponseMessage res = await clerk.Http.PutAsJsonAsync(
            $"/clients/{clerk.ClientId}/members/{target}/sets", new AssignSetsRequest([controller.Id]));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
    }

    [Fact]
    public async Task Grant_within_caller_scope_succeeds()
    {
        SeededClient clerk = await SeedUserAdminClerkAsync();
        // The clerk holds ar.write, so granting an AR-only member is fine.
        HttpResponseMessage res = await clerk.Http.PostAsJsonAsync($"/clients/{clerk.ClientId}/members",
            new AddClientMemberRequest(Guid.NewGuid(), ["ArClerk"], ["gl.read", "ar.read", "ar.write"]));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Deployment_admin_may_grant_anything()
    {
        SeededClient c = await fixture.SeedClientAsync(role: LedgerRole.Admin);
        HttpClient deploymentAdmin = fixture.AdminClient();
        // A deployment admin is exempt — can grant gl.post even though it holds no per-client membership.
        HttpResponseMessage res = await deploymentAdmin.PostAsJsonAsync($"/clients/{c.ClientId}/members",
            new AddClientMemberRequest(Guid.NewGuid(), ["Controller"], ["gl.read", "gl.post"]));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }
}
