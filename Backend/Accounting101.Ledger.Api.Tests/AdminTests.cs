using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>
/// Deployment control-plane: a deployment admin provisions a client and grants a user a role over
/// HTTP, after which that user can operate the books. Non-admins are refused the provisioning surface.
/// </summary>
public sealed class AdminTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task An_admin_provisions_a_client_and_a_member_who_can_then_post()
    {
        HttpClient admin = fixture.AdminClient();

        HttpResponseMessage createdClient = await admin.PostAsJsonAsync("/admin/clients", new CreateClientRequest { Name = "Acme" });
        Assert.Equal(HttpStatusCode.Created, createdClient.StatusCode);
        ClientRegistrationResponse client = (await createdClient.Content.ReadFromJsonAsync<ClientRegistrationResponse>())!;

        Guid user = Guid.NewGuid();
        HttpResponseMessage member = await admin.PostAsJsonAsync(
            $"/admin/clients/{client.Id}/members", new AddMemberRequest(user, "Controller"));
        Assert.Equal(HttpStatusCode.OK, member.StatusCode);

        // The freshly provisioned user can now post to the freshly provisioned client.
        HttpClient userClient = fixture.ClientFor(user, "Provisioned User");
        PostEntryRequest entry = new(null, new DateOnly(2026, 3, 31), null, null,
            [new PostLineRequest(Guid.NewGuid(), "Debit", 100m), new PostLineRequest(Guid.NewGuid(), "Credit", 100m)]);
        HttpResponseMessage posted = await userClient.PostAsJsonAsync($"/clients/{client.Id}/entries", entry);
        Assert.Equal(HttpStatusCode.Created, posted.StatusCode);
    }

    [Fact]
    public async Task A_non_admin_cannot_provision()
    {
        HttpClient notAdmin = fixture.ClientFor(Guid.NewGuid(), "Regular User"); // no admin claim
        HttpResponseMessage create = await notAdmin.PostAsJsonAsync("/admin/clients", new CreateClientRequest { Name = "Sneaky" });
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
    }

    [Fact]
    public async Task An_admin_lists_clients_and_members()
    {
        HttpClient admin = fixture.AdminClient();
        ClientRegistrationResponse client = (await (await admin.PostAsJsonAsync(
            "/admin/clients", new CreateClientRequest { Name = "Beta" })).Content.ReadFromJsonAsync<ClientRegistrationResponse>())!;
        Guid user = Guid.NewGuid();
        await admin.PostAsJsonAsync($"/admin/clients/{client.Id}/members", new AddMemberRequest(user, "Auditor"));

        ClientRegistrationResponse[] clients = (await admin.GetFromJsonAsync<ClientRegistrationResponse[]>("/admin/clients"))!;
        Assert.Contains(clients, c => c.Id == client.Id);

        MembershipResponse[] members = (await admin.GetFromJsonAsync<MembershipResponse[]>($"/admin/clients/{client.Id}/members"))!;
        Assert.Contains(members, m => m.UserId == user && m.Roles.Contains("Auditor"));
    }

    [Fact]
    public async Task Create_client_stores_and_returns_the_fiscal_year_end_month()
    {
        HttpResponseMessage created = await fixture.AdminClient().PostAsJsonAsync("/admin/clients",
            new CreateClientRequest { Name = "JuneCo", FiscalYearEndMonth = 6 });
        created.EnsureSuccessStatusCode();
        ClientRegistrationResponse body = (await created.Content.ReadFromJsonAsync<ClientRegistrationResponse>())!;
        Assert.Equal(6, body.FiscalYearEndMonth);
    }

    [Fact]
    public async Task Create_client_defaults_fiscal_year_end_to_december()
    {
        HttpResponseMessage created = await fixture.AdminClient().PostAsJsonAsync("/admin/clients",
            new CreateClientRequest { Name = "DefaultCo" });   // FiscalYearEndMonth omitted
        ClientRegistrationResponse body = (await created.Content.ReadFromJsonAsync<ClientRegistrationResponse>())!;
        Assert.Equal(12, body.FiscalYearEndMonth);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    public async Task Create_client_rejects_an_out_of_range_fiscal_year_end_month(int month)
    {
        HttpResponseMessage created = await fixture.AdminClient().PostAsJsonAsync("/admin/clients",
            new CreateClientRequest { Name = "BadCo", FiscalYearEndMonth = month });
        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
    }

    [Fact]
    public async Task An_admin_changes_a_clients_fiscal_year_end_after_creation()
    {
        HttpClient admin = fixture.AdminClient();
        ClientRegistrationResponse client = (await (await admin.PostAsJsonAsync(
            "/admin/clients", new CreateClientRequest { Name = "ShiftCo" }))   // defaults to December
            .Content.ReadFromJsonAsync<ClientRegistrationResponse>())!;
        Assert.Equal(12, client.FiscalYearEndMonth);

        HttpResponseMessage changed = await admin.PutAsJsonAsync(
            $"/admin/clients/{client.Id}/fiscal-year-end", new SetFiscalYearEndRequest(6));
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal(6, (await changed.Content.ReadFromJsonAsync<ClientRegistrationResponse>())!.FiscalYearEndMonth);

        // The new month persists — a subsequent list reflects it (and so close-year validation, which reads
        // the same scalar via FiscalYear.MonthOf, now uses June).
        ClientRegistrationResponse[] clients = (await admin.GetFromJsonAsync<ClientRegistrationResponse[]>("/admin/clients"))!;
        Assert.Equal(6, clients.Single(c => c.Id == client.Id).FiscalYearEndMonth);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    public async Task Change_fiscal_year_end_rejects_an_out_of_range_month(int month)
    {
        HttpClient admin = fixture.AdminClient();
        ClientRegistrationResponse client = (await (await admin.PostAsJsonAsync(
            "/admin/clients", new CreateClientRequest { Name = "RangeCo" }))
            .Content.ReadFromJsonAsync<ClientRegistrationResponse>())!;

        HttpResponseMessage changed = await admin.PutAsJsonAsync(
            $"/admin/clients/{client.Id}/fiscal-year-end", new SetFiscalYearEndRequest(month));
        Assert.Equal(HttpStatusCode.BadRequest, changed.StatusCode);
    }

    [Fact]
    public async Task Change_fiscal_year_end_returns_404_for_an_unknown_client()
    {
        HttpResponseMessage changed = await fixture.AdminClient().PutAsJsonAsync(
            $"/admin/clients/{Guid.NewGuid()}/fiscal-year-end", new SetFiscalYearEndRequest(6));
        Assert.Equal(HttpStatusCode.NotFound, changed.StatusCode);
    }

    [Fact]
    public async Task Create_client_without_mode_defaults_to_two_person()
    {
        HttpResponseMessage resp = await fixture.AdminClient().PostAsJsonAsync(
            "/admin/clients", new CreateClientRequest { Name = "Defaults Co", DatabaseName = null, FiscalYearEndMonth = 12 });
        resp.EnsureSuccessStatusCode();
        ClientRegistrationResponse body = (await resp.Content.ReadFromJsonAsync<ClientRegistrationResponse>())!;
        Assert.Equal(ApprovalMode.TwoPerson, body.ApprovalMode);
    }

    [Fact]
    public async Task Create_client_echoes_explicit_mode_as_a_string()
    {
        HttpResponseMessage resp = await fixture.AdminClient().PostAsJsonAsync(
            "/admin/clients", new CreateClientRequest { Name = "SelfCo", DatabaseName = null, ApprovalMode = ApprovalMode.SelfApprove, FiscalYearEndMonth = 12 });
        resp.EnsureSuccessStatusCode();
        string json = await resp.Content.ReadAsStringAsync();
        Assert.Contains("SelfApprove", json);                 // string on the wire
        Assert.DoesNotContain("RequireSegregationOfDuties", json); // bool is gone
    }
}
