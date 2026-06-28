using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;
using static Accounting101.Ledger.Api.Tests.TemporalScenario;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>The inception floor: onboarding seeds a closed-period freeze at (opening date − 1), so a post
/// dated before the client's opening date is rejected while one on/after is accepted — and the opening
/// entry itself is not blocked by its own freeze.</summary>
public sealed class InceptionFloorE2eTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Posts_before_the_opening_date_are_rejected_and_on_or_after_are_allowed()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid cash = await CreateAccountAsync(c.Http, c.ClientId, "1000", "Cash", "Asset");
        Guid revenue = await CreateAccountAsync(c.Http, c.ClientId, "4000", "Revenue", "Revenue");
        Guid equity = await CreateAccountAsync(c.Http, c.ClientId, "3000", "Retained Earnings", "Equity", retained: true);

        // Onboard as of 2024-01-01 → the opening entry posts (not blocked by its own freeze) and the
        // inception freeze is seeded at 2023-12-31.
        HttpResponseMessage onboard = await OnboardAsync(c.Http, c.ClientId, new DateOnly(2024, 1, 1),
            (cash, 100m), (equity, -100m));
        Assert.Equal(HttpStatusCode.Created, onboard.StatusCode);
        EntryResponse opening = (await onboard.Content.ReadFromJsonAsync<EntryResponse>())!;
        Assert.Equal("Opening", opening.Type);

        // A post dated on the inception freeze (2023-12-31) is rejected as a closed period.
        HttpResponseMessage before = await PostAsync(c.Http, c.ClientId, new DateOnly(2023, 12, 31), cash, revenue, 10m);
        await AssertProblemAsync(before, HttpStatusCode.Conflict, "closed");

        // A post dated on the opening date (2024-01-01) is accepted.
        HttpResponseMessage onOpening = await PostAsync(c.Http, c.ClientId, new DateOnly(2024, 1, 1), cash, revenue, 10m);
        Assert.Equal(HttpStatusCode.Created, onOpening.StatusCode);
    }
}
