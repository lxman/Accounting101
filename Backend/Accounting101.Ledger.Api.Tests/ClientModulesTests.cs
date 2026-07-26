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
        // whole request landed. All or nothing. Seeded with a NON-EMPTY starting entitlement — an
        // "still empty" outcome cannot distinguish "no write occurred" from "a write occurred that
        // happened to persist the empty set". A distinct original value that survives the rejected
        // request proves the write never landed.
        ControlStore control = fixture.Control();
        SeededClient client = await fixture.SeedClientAsync(enabledModules: ["receivables"]);
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
        Assert.Equal(["receivables"], saved!.EnabledModules);
    }

    [Fact]
    public async Task Guard_reads_the_composed_module_set_not_the_control_db()
    {
        // Task 4's central behavioural change: the guard now reads InstalledModules (what THIS host
        // actually composed), not control.ListModulesAsync() (the control DB's modules collection,
        // which ModuleRegistrar seeds from the full set of ModuleRegistration types the TEST PROCESS
        // references — every module project, regardless of what any one host's InstalledModules says).
        // fixture.AdminClient() boots the default host, where those two sources hold identical
        // content, so neither of the two facts above can tell the DI-sourced guard apart from the
        // control-DB-sourced guard it replaced. This test forces them apart: "cash" is genuinely
        // registered in the control DB (ModuleAvailabilityTests relies on the same fact — see
        // "cash"/"reconciliation" there), but fixture.ClientWithInstalledModules replaces THIS host's
        // InstalledModules with a reduced set that omits it. Under the OLD control-DB-sourced guard,
        // "cash" would be found in the control DB and this request would have returned 200; only the
        // DI-sourced guard rejects it.
        SeededClient client = await fixture.SeedClientAsync(enabledModules: []);
        HttpClient admin = fixture.ClientWithInstalledModules(Guid.NewGuid(), ["receivables"], ("admin", "true"));

        HttpResponseMessage response = await admin.PutAsJsonAsync(
            $"/admin/clients/{client.ClientId}/modules",
            new SetClientModulesRequest(["cash"]));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("cash", await response.Content.ReadAsStringAsync());
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

    [Fact]
    public async Task Admin_read_shows_raw_entitlement_beside_what_is_installed()
    {
        SeededClient client = await fixture.SeedClientAsync(
            enabledModules: ["cash", "reconciliation", "receivables"]);
        // A deployment that sold all three but composed only two — the misconfiguration the UI hides.
        // Installed is passed deliberately out of alphabetical order to prove the response sorts it
        // (rather than merely happening to look sorted because the input already was).
        HttpClient admin = fixture.ClientWithInstalledModules(
            Guid.NewGuid(), ["reconciliation", "cash"], ("admin", "true"));

        ClientModulesResponse body = (await admin.GetFromJsonAsync<ClientModulesResponse>(
            $"/admin/clients/{client.ClientId}/modules"))!;

        Assert.Equal(client.ClientId, body.ClientId);
        // Entitlement is NOT intersected here — support must see "sold but absent" ("receivables").
        Assert.Equal(["cash", "reconciliation", "receivables"], body.ModuleKeys);
        // Installed keys come back ordinally sorted regardless of input order.
        Assert.Equal(["cash", "reconciliation"], body.InstalledModuleKeys);
    }

    [Fact]
    public async Task Admin_read_of_an_unknown_client_is_not_found()
    {
        HttpClient admin = fixture.AdminClient();
        HttpResponseMessage response = await admin.GetAsync($"/admin/clients/{Guid.NewGuid()}/modules");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Admin_read_is_forbidden_to_a_plain_member()
    {
        SeededClient client = await fixture.SeedClientAsync(role: LedgerRole.Clerk, enabledModules: []);
        HttpResponseMessage response = await client.Http.GetAsync(
            $"/admin/clients/{client.ClientId}/modules");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
