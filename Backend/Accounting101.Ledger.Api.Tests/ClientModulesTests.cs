using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

public sealed class ClientModulesTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Deployment_admin_sets_modules_and_they_persist()
    {
        ControlStore control = fixture.Control();
        await control.RegisterModuleAsync(new ModuleRegistration { Key = "receivables", Name = "Receivables", Enabled = true });
        SeededClient client = await fixture.SeedClientAsync(enabledModules: []);
        HttpClient admin = fixture.AdminClient();

        HttpResponseMessage response = await admin.PutAsJsonAsync(
            $"/admin/clients/{client.ClientId}/modules",
            new SetClientModulesRequest(["receivables"]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ClientRegistration? saved = await control.GetClientAsync(client.ClientId);
        Assert.NotNull(saved);
        Assert.Equal(["receivables"], saved!.EnabledModules);
    }

    [Fact]
    public async Task Unknown_module_key_is_rejected()
    {
        SeededClient client = await fixture.SeedClientAsync(enabledModules: []);
        HttpClient admin = fixture.AdminClient();

        HttpResponseMessage response = await admin.PutAsJsonAsync(
            $"/admin/clients/{client.ClientId}/modules",
            new SetClientModulesRequest(["not-a-real-module"]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_client_is_not_found()
    {
        HttpClient admin = fixture.AdminClient();
        HttpResponseMessage response = await admin.PutAsJsonAsync(
            $"/admin/clients/{Guid.NewGuid()}/modules", new SetClientModulesRequest([]));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_plain_member_without_admin_client_is_forbidden()
    {
        ControlStore control = fixture.Control();
        await control.RegisterModuleAsync(new ModuleRegistration { Key = "receivables", Name = "Receivables", Enabled = true });
        SeededClient client = await fixture.SeedClientAsync(role: LedgerRole.Clerk, enabledModules: []);

        HttpResponseMessage response = await client.Http.PutAsJsonAsync(
            $"/admin/clients/{client.ClientId}/modules", new SetClientModulesRequest(["receivables"]));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
