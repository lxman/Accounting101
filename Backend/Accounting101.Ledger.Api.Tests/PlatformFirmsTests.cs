using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Api.Platform;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

public sealed class PlatformFirmsTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private HttpClient Operator() => fixture.ClientFor(Guid.NewGuid(), "Operator", ("platform", "true"));

    [Fact]
    public async Task Non_operator_is_forbidden_and_anonymous_is_unauthorized()
    {
        HttpClient firmAdmin = fixture.ClientFor(Guid.NewGuid(), "Firm Admin", ("admin", "true"));
        Assert.Equal(HttpStatusCode.Forbidden, (await firmAdmin.GetAsync("/platform/firms")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.AnonymousClient().GetAsync("/platform/firms")).StatusCode);
    }

    [Fact]
    public async Task Operator_lists_firms_including_the_default_firm()
    {
        HttpResponseMessage resp = await Operator().GetAsync("/platform/firms");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        List<FirmResponse> firms = (await resp.Content.ReadFromJsonAsync<List<FirmResponse>>())!;
        Assert.Contains(firms, f => f.Id == TenancyDefaults.DefaultFirmId);
    }

    [Fact]
    public async Task Provisions_a_firm_and_seeds_its_capability_sets()
    {
        HttpResponseMessage resp = await Operator().PostAsJsonAsync(
            "/platform/firms", new ProvisionFirmRequest { Name = "Ledger Pros" });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        FirmResponse firm = (await resp.Content.ReadFromJsonAsync<FirmResponse>())!;
        Assert.Equal("Ledger Pros", firm.Name);
        Assert.Equal("Active", firm.Status);
        Assert.StartsWith("firm_", firm.ControlDatabase);

        // The new firm's control DB has the built-in capability sets — it is usable immediately.
        ControlStore control = new(fixture.Mongo.GetDatabase(firm.ControlDatabase));
        Assert.NotNull(await control.GetCapabilitySetByNameAsync("Admin"));
        Assert.NotNull(await control.GetCapabilitySetByNameAsync("Clerk"));

        // And it is listed.
        List<FirmResponse> firms = (await (await Operator().GetAsync("/platform/firms"))
            .Content.ReadFromJsonAsync<List<FirmResponse>>())!;
        Assert.Contains(firms, f => f.Id == firm.Id);
    }

    [Fact]
    public async Task Provisioning_with_an_unknown_cluster_is_400()
    {
        HttpResponseMessage resp = await Operator().PostAsJsonAsync(
            "/platform/firms", new ProvisionFirmRequest { Name = "X", ClusterKey = "no-such-cluster" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Suspending_a_firm_blocks_its_requests_at_the_middleware()
    {
        FirmResponse firm = (await (await Operator().PostAsJsonAsync(
            "/platform/firms", new ProvisionFirmRequest { Name = "Doomed" }))
            .Content.ReadFromJsonAsync<FirmResponse>())!;

        HttpResponseMessage patch = await Operator().PatchAsJsonAsync(
            $"/platform/firms/{firm.Id}/status", new SetFirmStatusRequest("Suspended"));
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        Assert.Equal("Suspended", (await patch.Content.ReadFromJsonAsync<FirmResponse>())!.Status);

        // A request carrying the suspended firm's claim is refused at firm resolution, before any endpoint.
        HttpClient suspended = fixture.ClientFor(Guid.NewGuid(), "Member", (FirmClaims.FirmId, firm.Id.ToString()));
        HttpResponseMessage blocked = await suspended.GetAsync($"/clients/{Guid.NewGuid()}/accounts");
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
    }

    [Fact]
    public async Task Set_status_on_an_unknown_firm_is_404()
    {
        HttpResponseMessage resp = await Operator().PatchAsJsonAsync(
            $"/platform/firms/{Guid.NewGuid()}/status", new SetFirmStatusRequest("Suspended"));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Provisioning_registers_installed_modules_into_the_new_firms_control_db()
    {
        // Mint the operator HttpClient FIRST: WebApplicationFactory boots the host lazily, on first
        // client creation, and host boot is what runs ModuleRegistrar to seed the default firm's
        // control DB with every installed module. Reading modules before that point would see none.
        HttpClient op = Operator();

        // The default firm's control DB was seeded at startup by ModuleRegistrar; use it as the source of truth
        // for the installed module set (keys + process-global secrets).
        ControlStore defaultControl = fixture.Control();
        IReadOnlyList<ModuleRegistration> expected = await defaultControl.ListModulesAsync();
        Assert.NotEmpty(expected); // the host installs modules; sanity guard

        HttpResponseMessage provisioned = await op.PostAsJsonAsync(
            "/platform/firms", new ProvisionFirmRequest { Name = "Modules Firm" });
        Assert.Equal(HttpStatusCode.Created, provisioned.StatusCode);
        FirmResponse firm = (await provisioned.Content.ReadFromJsonAsync<FirmResponse>())!;

        ControlStore newControl = new(fixture.Mongo.GetDatabase(firm.ControlDatabase));
        IReadOnlyList<ModuleRegistration> actual = await newControl.ListModulesAsync();

        // Same modules, same secrets, all enabled — the new firm is immediately module-usable.
        Assert.Equal(
            expected.OrderBy(m => m.Key).Select(m => (m.Key, m.Secret, m.Enabled)),
            actual.OrderBy(m => m.Key).Select(m => (m.Key, m.Secret, m.Enabled)));
    }
}
