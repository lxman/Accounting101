using System.Net;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Api.Platform;

namespace Accounting101.Ledger.Api.Tests;

public sealed class CrossFirmIsolationTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task A_firm_A_token_cannot_read_a_firm_B_client()
    {
        // Firm B: its own control DB, one client, one member.
        (Guid firmBId, string firmBControl) = await fixture.SeedFirmAsync("Firm B");
        Guid clientB = Guid.NewGuid();
        Guid userB = Guid.NewGuid();
        ControlStore controlB = new(fixture.Mongo.GetDatabase(firmBControl));
        await controlB.RegisterClientAsync(new ClientRegistration
        {
            Id = clientB, Name = "B Books", DatabaseName = "client_" + clientB.ToString("N"),
        });
        await controlB.AddMembershipAsync(userB, clientB, LedgerRole.Controller);

        // A user acting in firm A (the default firm) presents firm B's real clientId.
        HttpClient firmAClient = fixture.ClientFor(Guid.NewGuid(), "A User",
            (FirmClaims.FirmId, TenancyDefaults.DefaultFirmId.ToString()));

        HttpResponseMessage response = await firmAClient.GetAsync($"/clients/{clientB}/accounts");

        // Isolation: the request is resolved entirely within firm A's control DB, which knows nothing of
        // firm B's client. Denial surfaces as 403 (no membership for that id in firm A) — and would be 404
        // if a membership somehow existed but the client did not — but NEVER 200 and never firm B's data.
        // (The resolver's cross-firm refusal itself is unit-tested in FirmScopedClientDatabaseResolverTests.)
        Assert.True(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound,
            $"expected 403/404, got {(int)response.StatusCode}");
    }

    [Fact]
    public async Task A_firm_B_token_reaches_its_own_client()
    {
        (Guid firmBId, string firmBControl) = await fixture.SeedFirmAsync("Firm B2");
        Guid clientB = Guid.NewGuid();
        Guid userB = Guid.NewGuid();
        ControlStore controlB = new(fixture.Mongo.GetDatabase(firmBControl));
        await controlB.RegisterClientAsync(new ClientRegistration
        {
            Id = clientB, Name = "B2 Books", DatabaseName = "client_" + clientB.ToString("N"),
        });
        await controlB.AddMembershipAsync(userB, clientB, LedgerRole.Controller);

        HttpClient firmBClient = fixture.ClientFor(userB, "B2 User",
            (FirmClaims.FirmId, firmBId.ToString()), ("role", "Controller"));

        HttpResponseMessage response = await firmBClient.GetAsync($"/clients/{clientB}/accounts");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
