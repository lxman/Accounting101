using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Contracts;
using Accounting101.TestSupport;
using EphemeralMongo;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Accounting101.Ledger.Api.Tests;

public sealed class PlatformToggleTests
{
    private static async Task<WebApplicationFactory<Program>> HostAsync(bool platformEnabled)
    {
        IMongoRunner runner = await SharedMongo.InstanceAsync();
        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseSetting("Mongo:ConnectionString", runner.ConnectionString)
             .UseSetting("Mongo:ControlDatabase", "control_" + Guid.NewGuid().ToString("N"))
             .UseSetting("Mongo:PlatformDatabase", "platform_" + Guid.NewGuid().ToString("N"))
             .UseSetting("Tenancy:Platform:Enabled", platformEnabled ? "true" : "false"));
    }

    private static HttpClient Operator(WebApplicationFactory<Program> host)
    {
        HttpClient http = host.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            DevTokenDefaults.Scheme,
            DevToken.Encode(new DevTokenPayload(Guid.NewGuid(), "Operator", [new DevClaim("platform", "true")])));
        return http;
    }

    [Fact]
    public async Task Disabled_platform_returns_404_on_platform_routes_even_with_an_operator_token()
    {
        await using WebApplicationFactory<Program> host = await HostAsync(platformEnabled: false);
        HttpClient op = Operator(host);

        Assert.Equal(HttpStatusCode.NotFound, (await op.GetAsync("/platform/firms")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await op.PostAsJsonAsync("/platform/firms", new ProvisionFirmRequest { Name = "X" })).StatusCode);
    }

    [Fact]
    public async Task Disabled_platform_leaves_the_rest_of_the_app_routing()
    {
        await using WebApplicationFactory<Program> host = await HostAsync(platformEnabled: false);
        HttpClient anon = host.CreateClient();

        // A non-platform control-plane route is still mapped — it challenges auth (401), it is NOT 404.
        HttpResponseMessage response = await anon.GetAsync("/admin/clients");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Enabled_platform_maps_the_routes()
    {
        await using WebApplicationFactory<Program> host = await HostAsync(platformEnabled: true);
        HttpClient op = Operator(host);

        HttpResponseMessage response = await op.GetAsync("/platform/firms");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
