using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;
using Accounting101.Ledger.Api.Control;

namespace Accounting101.Ledger.Api.Tests;

public sealed class CapabilitiesTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task A_controller_gets_their_resolved_capabilities_and_role()
    {
        SeededClient c = await fixture.SeedClientAsync(role: LedgerRole.Controller);
        CapabilitiesResponse body = (await c.Http.GetFromJsonAsync<CapabilitiesResponse>(
            $"/clients/{c.ClientId}/me/capabilities"))!;

        Assert.Contains("Controller", body.Roles);
        Assert.Contains("gl.post", body.Capabilities);
        Assert.Contains("ar.write", body.Capabilities);
        Assert.False(body.DeploymentAdmin);
    }

    [Fact]
    public async Task An_auditor_gets_reads_only()
    {
        SeededClient c = await fixture.SeedClientAsync(role: LedgerRole.Auditor);
        CapabilitiesResponse body = (await c.Http.GetFromJsonAsync<CapabilitiesResponse>(
            $"/clients/{c.ClientId}/me/capabilities"))!;

        Assert.Contains("gl.read", body.Capabilities);
        Assert.DoesNotContain("gl.post", body.Capabilities);
        Assert.DoesNotContain("ar.write", body.Capabilities);
    }

    [Fact]
    public async Task A_narrow_ar_clerk_can_write_ar_but_not_ap()
    {
        SeededClient c = await fixture.SeedClientAsync(role: LedgerRole.ArClerk);
        CapabilitiesResponse body = (await c.Http.GetFromJsonAsync<CapabilitiesResponse>(
            $"/clients/{c.ClientId}/me/capabilities"))!;

        Assert.Contains("ar.write", body.Capabilities);
        Assert.Contains("ar.read", body.Capabilities);
        Assert.Contains("gl.read", body.Capabilities);
        Assert.DoesNotContain("ap.write", body.Capabilities);
        Assert.DoesNotContain("ap.read", body.Capabilities);   // tight scope: no other-module reads
        Assert.DoesNotContain("audit.read", body.Capabilities);
    }

    [Fact]
    public async Task Overlapping_roles_return_the_union()
    {
        Guid client = (await fixture.SeedClientAsync(role: LedgerRole.Auditor)).ClientId;
        Guid user = Guid.NewGuid();
        await fixture.Control().AddMembershipRolesAsync(user, client, [LedgerRole.ArClerk, LedgerRole.ApClerk]);
        HttpClient http = fixture.ClientFor(user, "Dual Clerk");

        CapabilitiesResponse body = (await http.GetFromJsonAsync<CapabilitiesResponse>(
            $"/clients/{client}/me/capabilities"))!;
        Assert.Contains("ar.write", body.Capabilities);
        Assert.Contains("ap.write", body.Capabilities);
        Assert.Contains("ArClerk", body.Roles);
        Assert.Contains("ApClerk", body.Roles);
    }

    [Fact]
    public async Task Deployment_admin_flag_reflects_the_claim()
    {
        SeededClient c = await fixture.SeedClientAsync(role: LedgerRole.Controller);
        // Re-issue the same member's token WITH the deployment-admin claim.
        HttpClient http = fixture.ClientFor(c.UserId, "Admin Member", ("admin", "true"));
        CapabilitiesResponse body = (await http.GetFromJsonAsync<CapabilitiesResponse>(
            $"/clients/{c.ClientId}/me/capabilities"))!;
        Assert.True(body.DeploymentAdmin);
    }

    [Fact]
    public async Task A_non_member_is_forbidden()
    {
        SeededClient c = await fixture.SeedClientAsync();
        HttpClient stranger = fixture.ClientFor(Guid.NewGuid(), "Stranger");
        HttpResponseMessage res = await stranger.GetAsync($"/clients/{c.ClientId}/me/capabilities");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Theory]
    [InlineData("receivables", ModuleAccessLevel.Write, "ar.write")]
    [InlineData("receivables", ModuleAccessLevel.Read, "ar.read")]
    [InlineData("payables", ModuleAccessLevel.Write, "ap.write")]
    [InlineData("payables", ModuleAccessLevel.Read, "ap.read")]
    [InlineData("payroll", ModuleAccessLevel.Write, "payroll.write")]
    [InlineData("payroll", ModuleAccessLevel.Read, "payroll.read")]
    [InlineData("cash", ModuleAccessLevel.Write, "cash.write")]
    [InlineData("cash", ModuleAccessLevel.Read, "cash.read")]
    [InlineData("reconciliation", ModuleAccessLevel.Write, "bankrec.write")]
    [InlineData("reconciliation", ModuleAccessLevel.Read, "bankrec.read")]
    public void CapabilityForModule_maps_each_module_and_level(string key, ModuleAccessLevel level, string expected) =>
        Assert.Equal(expected, Capabilities.CapabilityForModule(key, level));

    [Fact]
    public void CapabilityForModule_returns_null_for_an_unmapped_module_key()
    {
        Assert.Null(Capabilities.CapabilityForModule("invoicing", ModuleAccessLevel.Write));
        Assert.Null(Capabilities.CapabilityForModule("ghost", ModuleAccessLevel.Read));
    }
}
