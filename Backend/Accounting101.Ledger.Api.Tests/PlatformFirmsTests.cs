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
}
