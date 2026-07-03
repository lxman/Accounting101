using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

public sealed class CapabilitySetEndpointsTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task List_returns_the_seeded_builtins_to_a_deployment_admin()
    {
        HttpClient admin = fixture.AdminClient();
        List<CapabilitySetResponse> sets =
            (await admin.GetFromJsonAsync<List<CapabilitySetResponse>>("/capability-sets"))!;

        Assert.Contains(sets, s => s.Name == "Controller" && s.Builtin);
        Assert.Contains(sets, s => s.Name == "ArClerk" && s.Builtin);
    }

    [Fact]
    public async Task A_non_admin_member_is_forbidden()
    {
        SeededClient c = await fixture.SeedClientAsync(role: LedgerRole.Controller);
        HttpResponseMessage res = await c.Http.GetAsync("/capability-sets");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Create_then_list_includes_the_new_custom_set()
    {
        HttpClient admin = fixture.AdminClient();
        string name = "Warehouse " + Guid.NewGuid().ToString("N");
        HttpResponseMessage res = await admin.PostAsJsonAsync("/capability-sets",
            new CreateCapabilitySetRequest(name, "Receiving", [Capabilities.GlRead, Capabilities.ApWrite]));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);

        CapabilitySetResponse created = (await res.Content.ReadFromJsonAsync<CapabilitySetResponse>())!;
        Assert.False(created.Builtin);
        Assert.Equal(name, created.Name);
        Assert.True(new HashSet<string> { Capabilities.GlRead, Capabilities.ApWrite }.SetEquals(created.Capabilities));
    }

    [Fact]
    public async Task Create_with_an_unknown_capability_is_422()
    {
        HttpClient admin = fixture.AdminClient();
        HttpResponseMessage res = await admin.PostAsJsonAsync("/capability-sets",
            new CreateCapabilitySetRequest("Bad " + Guid.NewGuid().ToString("N"), null, ["gl.read", "not.a.capability"]));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
    }

    [Fact]
    public async Task Create_with_a_duplicate_name_is_409()
    {
        HttpClient admin = fixture.AdminClient();
        string name = "Dup " + Guid.NewGuid().ToString("N");
        await admin.PostAsJsonAsync("/capability-sets", new CreateCapabilitySetRequest(name, null, ["gl.read"]));
        HttpResponseMessage res = await admin.PostAsJsonAsync("/capability-sets",
            new CreateCapabilitySetRequest(name.ToUpperInvariant(), null, ["gl.read"]));
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task Update_edits_a_set_in_place()
    {
        HttpClient admin = fixture.AdminClient();
        string name = "Editable " + Guid.NewGuid().ToString("N");
        CapabilitySetResponse created = (await (await admin.PostAsJsonAsync("/capability-sets",
            new CreateCapabilitySetRequest(name, null, ["gl.read"]))).Content.ReadFromJsonAsync<CapabilitySetResponse>())!;

        HttpResponseMessage res = await admin.PutAsJsonAsync($"/capability-sets/{created.Id}",
            new UpdateCapabilitySetRequest(name, "now with post", [Capabilities.GlRead, Capabilities.GlPost]));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        CapabilitySetResponse updated = (await res.Content.ReadFromJsonAsync<CapabilitySetResponse>())!;
        Assert.Equal("now with post", updated.Description);
        Assert.True(new HashSet<string> { Capabilities.GlRead, Capabilities.GlPost }.SetEquals(updated.Capabilities));
    }

    [Fact]
    public async Task Update_edits_a_builtin_in_place_and_keeps_it_builtin()
    {
        HttpClient admin = fixture.AdminClient();
        List<CapabilitySetResponse> sets =
            (await admin.GetFromJsonAsync<List<CapabilitySetResponse>>("/capability-sets"))!;
        CapabilitySetResponse payrollClerk = sets.First(s => s.Name == "PayrollClerk");

        HttpResponseMessage res = await admin.PutAsJsonAsync($"/capability-sets/{payrollClerk.Id}",
            new UpdateCapabilitySetRequest("PayrollClerk", "edited", [Capabilities.GlRead]));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        CapabilitySetResponse updated = (await res.Content.ReadFromJsonAsync<CapabilitySetResponse>())!;
        Assert.True(new HashSet<string> { Capabilities.GlRead }.SetEquals(updated.Capabilities));
        Assert.True(updated.Builtin);
    }

    [Fact]
    public async Task Create_with_omitted_capabilities_defaults_to_empty()
    {
        HttpClient admin = fixture.AdminClient();
        HttpResponseMessage res = await admin.PostAsJsonAsync("/capability-sets",
            new CreateCapabilitySetRequest("NoCaps " + Guid.NewGuid().ToString("N"), null, null!));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);

        CapabilitySetResponse created = (await res.Content.ReadFromJsonAsync<CapabilitySetResponse>())!;
        Assert.Empty(created.Capabilities);
    }

    [Fact]
    public async Task Update_a_missing_set_is_404()
    {
        HttpClient admin = fixture.AdminClient();
        HttpResponseMessage res = await admin.PutAsJsonAsync($"/capability-sets/{Guid.NewGuid()}",
            new UpdateCapabilitySetRequest("Ghost", null, ["gl.read"]));
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Delete_a_custom_set_is_204()
    {
        HttpClient admin = fixture.AdminClient();
        CapabilitySetResponse created = (await (await admin.PostAsJsonAsync("/capability-sets",
            new CreateCapabilitySetRequest("Doomed " + Guid.NewGuid().ToString("N"), null, ["gl.read"])))
            .Content.ReadFromJsonAsync<CapabilitySetResponse>())!;

        HttpResponseMessage res = await admin.DeleteAsync($"/capability-sets/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
    }

    [Fact]
    public async Task Deleting_a_builtin_is_409()
    {
        HttpClient admin = fixture.AdminClient();
        List<CapabilitySetResponse> sets =
            (await admin.GetFromJsonAsync<List<CapabilitySetResponse>>("/capability-sets"))!;
        CapabilitySetResponse controller = sets.First(s => s.Name == "Controller");

        HttpResponseMessage res = await admin.DeleteAsync($"/capability-sets/{controller.Id}");
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task Deleting_a_set_that_a_member_references_is_409()
    {
        HttpClient admin = fixture.AdminClient();
        CapabilitySetResponse created = (await (await admin.PostAsJsonAsync("/capability-sets",
            new CreateCapabilitySetRequest("Held " + Guid.NewGuid().ToString("N"), null, ["gl.read"])))
            .Content.ReadFromJsonAsync<CapabilitySetResponse>())!;

        // A member now references the set.
        await fixture.Control().SetMembershipSetsAsync(Guid.NewGuid(), Guid.NewGuid(), [created.Id]);

        HttpResponseMessage res = await admin.DeleteAsync($"/capability-sets/{created.Id}");
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task Editing_a_set_reports_the_affected_member_count()
    {
        HttpClient admin = fixture.AdminClient();
        CapabilitySetResponse created = (await (await admin.PostAsJsonAsync("/capability-sets",
            new CreateCapabilitySetRequest("Counted " + Guid.NewGuid().ToString("N"), null, ["gl.read"])))
            .Content.ReadFromJsonAsync<CapabilitySetResponse>())!;

        ControlStore control = fixture.Control();
        await control.SetMembershipSetsAsync(Guid.NewGuid(), Guid.NewGuid(), [created.Id]);
        await control.SetMembershipSetsAsync(Guid.NewGuid(), Guid.NewGuid(), [created.Id]);

        HttpResponseMessage res = await admin.PutAsJsonAsync($"/capability-sets/{created.Id}",
            new UpdateCapabilitySetRequest(created.Name, "edited", [Capabilities.GlRead, Capabilities.GlPost]));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        CapabilitySetResponse updated = (await res.Content.ReadFromJsonAsync<CapabilitySetResponse>())!;
        Assert.Equal(2, updated.AffectedMemberCount);
    }
}
