using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;
using Accounting101.Ledger.Api.Control;

namespace Accounting101.Ledger.Api.Tests;

public sealed class MemberManagementTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    // Seed a client whose primary member is an Admin (holds admin.users).
    private async Task<SeededClient> SeedWithAdminAsync()
        => await fixture.SeedClientAsync(role: LedgerRole.Admin);

    [Fact]
    public async Task Admin_lists_adds_edits_and_removes_members()
    {
        SeededClient c = await SeedWithAdminAsync();

        // list (self only, initially)
        MembershipResponse[] initial = (await c.Http.GetFromJsonAsync<MembershipResponse[]>($"/clients/{c.ClientId}/members"))!;
        Assert.Single(initial);

        // add
        Guid newUser = Guid.NewGuid();
        HttpResponseMessage add = await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/members",
            new AddClientMemberRequest(newUser, ["ArClerk"], ["gl.read", "ar.read", "ar.write"]));
        Assert.Equal(HttpStatusCode.OK, add.StatusCode);

        // edit (widen visibility: add ap.read)
        HttpResponseMessage edit = await c.Http.PutAsJsonAsync($"/clients/{c.ClientId}/members/{newUser}",
            new SetMemberRequest(["ArClerk"], ["gl.read", "ar.read", "ar.write", "ap.read"]));
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        MembershipResponse edited = (await edit.Content.ReadFromJsonAsync<MembershipResponse>())!;
        Assert.Contains("ap.read", edited.Capabilities);

        // remove
        HttpResponseMessage del = await c.Http.DeleteAsync($"/clients/{c.ClientId}/members/{newUser}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
    }

    [Fact]
    public async Task A_non_admin_member_is_forbidden()
    {
        SeededClient c = await fixture.SeedClientAsync(role: LedgerRole.Clerk);   // no admin.users
        HttpResponseMessage list = await c.Http.GetAsync($"/clients/{c.ClientId}/members");
        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
    }

    [Fact]
    public async Task Adding_an_existing_member_is_a_conflict()
    {
        SeededClient c = await SeedWithAdminAsync();
        HttpResponseMessage dup = await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/members",
            new AddClientMemberRequest(c.UserId, ["Auditor"], ["gl.read"]));
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
    }

    [Fact]
    public async Task Unknown_role_or_capability_is_rejected()
    {
        SeededClient c = await SeedWithAdminAsync();
        HttpResponseMessage badRole = await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/members",
            new AddClientMemberRequest(Guid.NewGuid(), ["Wizard"], ["gl.read"]));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badRole.StatusCode);
        HttpResponseMessage badCap = await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/members",
            new AddClientMemberRequest(Guid.NewGuid(), ["Auditor"], ["gl.fly"]));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badCap.StatusCode);
    }

    [Fact]
    public async Task Cannot_remove_the_last_admin()
    {
        SeededClient c = await SeedWithAdminAsync();   // sole admin is c.UserId
        // DELETE self (last admin) → 409
        HttpResponseMessage del = await c.Http.DeleteAsync($"/clients/{c.ClientId}/members/{c.UserId}");
        Assert.Equal(HttpStatusCode.Conflict, del.StatusCode);
        // PUT self removing admin.users → 409
        HttpResponseMessage strip = await c.Http.PutAsJsonAsync($"/clients/{c.ClientId}/members/{c.UserId}",
            new SetMemberRequest(["Auditor"], ["gl.read"]));
        Assert.Equal(HttpStatusCode.Conflict, strip.StatusCode);
    }

    [Fact]
    public async Task Catalog_returns_the_vocabulary_and_presets()
    {
        SeededClient c = await SeedWithAdminAsync();
        CapabilityCatalogResponse cat = (await c.Http.GetFromJsonAsync<CapabilityCatalogResponse>("/capabilities/catalog"))!;
        Assert.Contains("ar.write", cat.Capabilities);
        Assert.Contains(cat.Roles, r => r.Role == "Controller" && r.Capabilities.Contains("gl.post"));
    }
}
