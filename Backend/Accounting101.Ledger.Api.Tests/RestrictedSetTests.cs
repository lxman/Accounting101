using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

public sealed class RestrictedSetTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Builtin_Admin_set_is_restricted()
    {
        HttpClient admin = fixture.AdminClient();
        List<CapabilitySetResponse> sets =
            (await admin.GetFromJsonAsync<List<CapabilitySetResponse>>("/capability-sets"))!;
        Assert.True(sets.First(s => s.Name == "Admin").Restricted);
        Assert.False(sets.First(s => s.Name == "Controller").Restricted);
    }

    [Fact]
    public async Task Create_can_mark_a_set_restricted_and_it_round_trips()
    {
        HttpClient admin = fixture.AdminClient();
        string name = "Locked " + Guid.NewGuid().ToString("N");
        CapabilitySetResponse created = (await (await admin.PostAsJsonAsync("/capability-sets",
            new CreateCapabilitySetRequest(name, null, ["gl.read"], Restricted: true)))
            .Content.ReadFromJsonAsync<CapabilitySetResponse>())!;
        Assert.True(created.Restricted);
    }

    [Fact]
    public async Task Non_deployment_admin_cannot_assign_a_restricted_set()
    {
        // A full client Admin holds every capability, so #1 (no-escalation) would pass — the 403 is
        // purely the restricted-set tier guard.
        SeededClient clientAdmin = await fixture.SeedClientAsync(role: LedgerRole.Admin);
        CapabilitySet adminSet = (await fixture.Control().GetCapabilitySetByNameAsync("Admin"))!;
        Guid target = Guid.NewGuid();
        await clientAdmin.Http.PostAsJsonAsync($"/clients/{clientAdmin.ClientId}/members",
            new AddClientMemberRequest(target, ["Auditor"], ["gl.read"]));

        HttpResponseMessage res = await clientAdmin.Http.PutAsJsonAsync(
            $"/clients/{clientAdmin.ClientId}/members/{target}/sets", new AssignSetsRequest([adminSet.Id]));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Deployment_admin_can_assign_a_restricted_set()
    {
        SeededClient c = await fixture.SeedClientAsync(role: LedgerRole.Admin);
        HttpClient deploymentAdmin = fixture.AdminClient();
        CapabilitySet adminSet = (await fixture.Control().GetCapabilitySetByNameAsync("Admin"))!;
        Guid target = Guid.NewGuid();
        await deploymentAdmin.PostAsJsonAsync($"/clients/{c.ClientId}/members",
            new AddClientMemberRequest(target, ["Auditor"], ["gl.read"]));

        HttpResponseMessage res = await deploymentAdmin.PutAsJsonAsync(
            $"/clients/{c.ClientId}/members/{target}/sets", new AssignSetsRequest([adminSet.Id]));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }
}
