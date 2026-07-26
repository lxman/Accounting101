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
    public async Task An_uninstalled_module_key_is_rejected_and_named()
    {
        SeededClient client = await fixture.SeedClientAsync(enabledModules: []);
        HttpClient admin = fixture.AdminClient();

        HttpResponseMessage response = await admin.PutAsJsonAsync(
            $"/admin/clients/{client.ClientId}/modules",
            new SetClientModulesRequest(["not-a-real-module"]));

        // 422, not 400: the request is well-formed, it just asks to sell something this deployment
        // cannot run. The body names the key so an operator can act on it.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("not-a-real-module", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_mix_of_installed_and_uninstalled_keys_persists_nothing()
    {
        // A half-applied module set is worse than either clean outcome: the operator would believe the
        // whole request landed. All or nothing.
        ControlStore control = fixture.Control();
        SeededClient client = await fixture.SeedClientAsync(enabledModules: []);
        HttpClient admin = fixture.AdminClient();

        HttpResponseMessage response = await admin.PutAsJsonAsync(
            $"/admin/clients/{client.ClientId}/modules",
            new SetClientModulesRequest(["receivables", "not-a-real-module"]));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("not-a-real-module", body);
        Assert.DoesNotContain("receivables", body);

        ClientRegistration? saved = await control.GetClientAsync(client.ClientId);
        Assert.NotNull(saved);
        Assert.Empty(saved!.EnabledModules);
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
