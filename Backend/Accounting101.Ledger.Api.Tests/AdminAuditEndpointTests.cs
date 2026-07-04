using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

public sealed class AdminAuditEndpointTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Deployment_admin_can_query_the_audit_trail()
    {
        HttpClient admin = fixture.AdminClient();
        Guid target = Guid.NewGuid();
        await fixture.Audit().AppendAsync(new AdminAuditEntry
        {
            Id = Guid.NewGuid(), Timestamp = DateTime.UtcNow, ActorUserId = Guid.NewGuid(),
            Action = "MemberAdded", TargetUserId = target,
        });

        List<AdminAuditEntryResponse> entries =
            (await admin.GetFromJsonAsync<List<AdminAuditEntryResponse>>($"/admin/audit?targetUserId={target}"))!;
        Assert.Contains(entries, e => e.Action == "MemberAdded" && e.TargetUserId == target);
    }

    [Fact]
    public async Task A_non_deployment_admin_is_forbidden()
    {
        SeededClient c = await fixture.SeedClientAsync(role: LedgerRole.Admin);
        HttpResponseMessage res = await c.Http.GetAsync("/admin/audit");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }
}
