using System.Net;
using System.Net.Http.Json;
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
        Assert.Contains(members, m => m.UserId == user && m.Role == "Auditor");
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
}
