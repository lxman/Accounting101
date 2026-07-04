using System.Net;
using System.Net.Http.Json;
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
}
