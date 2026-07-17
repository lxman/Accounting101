using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>
/// Slice E: per-client admin endpoints accept a deployment admin OR a member holding the matching
/// admin.* capability, and refuse a member without it. Control-plane client provisioning stays
/// deployment-admin-only.
/// </summary>
public sealed class AdminCapabilityTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private async Task<(Guid clientId, HttpClient http)> MemberWithAsync(params string[] capabilities)
    {
        SeededClient c = await fixture.SeedClientAsync("AdminCaps");
        Guid userId = Guid.NewGuid();
        await fixture.Control().SetMembershipAsync(userId, c.ClientId, [], capabilities);
        return (c.ClientId, fixture.ClientFor(userId, "Member"));
    }

    [Fact]
    public async Task Member_with_admin_fiscal_may_set_fiscal_year_end()
    {
        (Guid clientId, HttpClient http) = await MemberWithAsync(Capabilities.AdminFiscal);
        HttpResponseMessage resp = await http.PutAsJsonAsync(
            $"/admin/clients/{clientId}/fiscal-year-end", new SetFiscalYearEndRequest(6));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Member_without_admin_fiscal_is_forbidden()
    {
        (Guid clientId, HttpClient http) = await MemberWithAsync(Capabilities.GlRead);
        HttpResponseMessage resp = await http.PutAsJsonAsync(
            $"/admin/clients/{clientId}/fiscal-year-end", new SetFiscalYearEndRequest(6));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Deployment_admin_may_set_fiscal_year_end()
    {
        SeededClient c = await fixture.SeedClientAsync("AdminCapsDeploy");
        HttpResponseMessage resp = await fixture.AdminClient().PutAsJsonAsync(
            $"/admin/clients/{c.ClientId}/fiscal-year-end", new SetFiscalYearEndRequest(6));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Member_with_admin_fiscal_may_read_fiscal_year_end()
    {
        (Guid clientId, HttpClient http) = await MemberWithAsync(Capabilities.AdminFiscal);
        await http.PutAsJsonAsync($"/admin/clients/{clientId}/fiscal-year-end", new SetFiscalYearEndRequest(6));

        HttpResponseMessage resp = await http.GetAsync($"/admin/clients/{clientId}/fiscal-year-end");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        FiscalYearEndResponse? body = await resp.Content.ReadFromJsonAsync<FiscalYearEndResponse>();
        Assert.Equal(6, body!.FiscalYearEndMonth);
    }

    [Fact]
    public async Task Member_without_admin_fiscal_is_forbidden_from_reading_fiscal_year_end()
    {
        (Guid clientId, HttpClient http) = await MemberWithAsync(Capabilities.GlRead);
        HttpResponseMessage resp = await http.GetAsync($"/admin/clients/{clientId}/fiscal-year-end");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Deployment_admin_may_read_fiscal_year_end()
    {
        SeededClient c = await fixture.SeedClientAsync("AdminCapsFiscalRead");
        HttpResponseMessage resp = await fixture.AdminClient().GetAsync($"/admin/clients/{c.ClientId}/fiscal-year-end");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Member_with_admin_users_may_list_members()
    {
        (Guid clientId, HttpClient http) = await MemberWithAsync(Capabilities.AdminUsers);
        HttpResponseMessage resp = await http.GetAsync($"/admin/clients/{clientId}/members");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Member_without_admin_users_is_forbidden_from_listing_members()
    {
        (Guid clientId, HttpClient http) = await MemberWithAsync(Capabilities.GlRead);
        HttpResponseMessage resp = await http.GetAsync($"/admin/clients/{clientId}/members");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Creating_a_client_still_requires_a_deployment_admin()
    {
        SeededClient c = await fixture.SeedClientAsync("NotDeployAdmin");
        HttpResponseMessage resp = await c.Http.PostAsJsonAsync(
            "/admin/clients", new CreateClientRequest { Name = "New Co", DatabaseName = null, ApprovalMode = ApprovalMode.SelfApprove, FiscalYearEndMonth = 12 });
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }
}
