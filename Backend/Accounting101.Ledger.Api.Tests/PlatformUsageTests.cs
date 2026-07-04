using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

public sealed class PlatformUsageTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private HttpClient Operator() => fixture.ClientFor(Guid.NewGuid(), "Operator", ("platform", "true"));

    [Fact]
    public async Task Usage_tallies_active_clients_and_enabled_modules_per_firm()
    {
        HttpClient op = Operator();
        HttpResponseMessage provisioned = await op.PostAsJsonAsync(
            "/platform/firms", new ProvisionFirmRequest { Name = "Meter Firm" });
        FirmResponse firm = (await provisioned.Content.ReadFromJsonAsync<FirmResponse>())!;

        // Two active clients (one with receivables, one with receivables+payables) and one archived.
        ControlStore control = new(fixture.Mongo.GetDatabase(firm.ControlDatabase));
        await control.RegisterClientAsync(new ClientRegistration
        {
            Id = Guid.NewGuid(), Name = "C1", DatabaseName = "d1",
            Status = ClientStatus.Active, EnabledModules = ["receivables"],
        });
        await control.RegisterClientAsync(new ClientRegistration
        {
            Id = Guid.NewGuid(), Name = "C2", DatabaseName = "d2",
            Status = ClientStatus.Active, EnabledModules = ["receivables", "payables"],
        });
        await control.RegisterClientAsync(new ClientRegistration
        {
            Id = Guid.NewGuid(), Name = "C3", DatabaseName = "d3",
            Status = ClientStatus.Archived, EnabledModules = ["receivables"],
        });

        UsageResponse usage = (await op.GetFromJsonAsync<UsageResponse>("/platform/usage"))!;
        FirmUsageResponse f = usage.Firms.Single(x => x.FirmId == firm.Id);

        Assert.Equal(2, f.ActiveClients);
        Assert.Equal(2, f.ModuleClientCounts["receivables"]); // only the two active clients
        Assert.Equal(1, f.ModuleClientCounts["payables"]);
    }

    [Fact]
    public async Task Usage_requires_the_platform_claim()
    {
        HttpClient nonOperator = fixture.ClientFor(Guid.NewGuid(), "Nobody");
        HttpResponseMessage response = await nonOperator.GetAsync("/platform/usage");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
