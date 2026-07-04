using System.Net.Http.Json;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

public sealed class AdminAuditWiringTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Assigning_sets_writes_an_audit_entry_naming_actor_and_target()
    {
        SeededClient c = await fixture.SeedClientAsync(role: LedgerRole.Admin);
        CapabilitySet arClerk = (await fixture.Control().GetCapabilitySetByNameAsync("ArClerk"))!;
        Guid target = Guid.NewGuid();
        await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/members",
            new AddClientMemberRequest(target, ["Auditor"], ["gl.read"]));

        await c.Http.PutAsJsonAsync($"/clients/{c.ClientId}/members/{target}/sets",
            new AssignSetsRequest([arClerk.Id]));

        IReadOnlyList<AdminAuditEntry> entries =
            await fixture.Audit().QueryAsync(new AdminAuditFilter(TargetUserId: target));
        Assert.Contains(entries, e => e.Action == "MemberSetsAssigned"
            && e.ClientId == c.ClientId && e.After!.SetIds!.Contains(arClerk.Id));
    }

    [Fact]
    public async Task Editing_a_set_writes_a_before_after_audit_entry()
    {
        HttpClient admin = fixture.AdminClient();
        string name = "Audited " + Guid.NewGuid().ToString("N");
        CapabilitySetResponse created = (await (await admin.PostAsJsonAsync("/capability-sets",
            new CreateCapabilitySetRequest(name, null, ["gl.read"]))).Content.ReadFromJsonAsync<CapabilitySetResponse>())!;

        await admin.PutAsJsonAsync($"/capability-sets/{created.Id}",
            new UpdateCapabilitySetRequest(name, null, ["gl.read", "gl.post"]));

        IReadOnlyList<AdminAuditEntry> entries =
            await fixture.Audit().QueryAsync(new AdminAuditFilter(Limit: 500));
        AdminAuditEntry edit = entries.First(e => e.Action == "SetUpdated" && e.TargetSetId == created.Id);
        Assert.Contains("gl.read", edit.Before!.Capabilities!);
        Assert.DoesNotContain("gl.post", edit.Before!.Capabilities!);
        Assert.Contains("gl.post", edit.After!.Capabilities!);
    }
}
