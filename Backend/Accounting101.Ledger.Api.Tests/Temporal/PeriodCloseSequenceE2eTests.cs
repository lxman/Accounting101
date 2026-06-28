using System.Net;
using static Accounting101.Ledger.Api.Tests.TemporalScenario;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>The close pointer is a single monotonic "closed-through" high-water date: closing at or before
/// the current close is refused, a later date is accepted. Plus: a year-end close with temporary-account
/// activity but no retained-earnings account is refused with a clear reason.</summary>
public sealed class PeriodCloseSequenceE2eTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task The_close_pointer_is_monotonic()
    {
        (Guid clientId, HttpClient http) = await SeedFyeClientAsync(fixture, 12, "MonoCo");
        Guid cash = await CreateAccountAsync(http, clientId, "1000", "Cash", "Asset");
        Guid revenue = await CreateAccountAsync(http, clientId, "4000", "Revenue", "Revenue");
        await PostAndApproveAsync(http, clientId, new DateOnly(2024, 2, 15), cash, revenue, 50m);
        await PostAndApproveAsync(http, clientId, new DateOnly(2024, 4, 15), cash, revenue, 50m);

        // Close through March 31.
        Assert.Equal(HttpStatusCode.OK, (await CloseAsync(http, clientId, new DateOnly(2024, 3, 31))).StatusCode);

        // Closing at an earlier date is refused (already closed through 2024-03-31).
        await AssertProblemAsync(await CloseAsync(http, clientId, new DateOnly(2024, 2, 28)),
            HttpStatusCode.Conflict, "already closed");

        // Closing at the SAME date is refused too.
        await AssertProblemAsync(await CloseAsync(http, clientId, new DateOnly(2024, 3, 31)),
            HttpStatusCode.Conflict, "already closed");

        // Closing at a LATER date is accepted.
        Assert.Equal(HttpStatusCode.OK, (await CloseAsync(http, clientId, new DateOnly(2024, 4, 30))).StatusCode);
    }

    [Fact]
    public async Task Close_year_without_a_retained_earnings_account_is_refused()
    {
        (Guid clientId, HttpClient http) = await SeedFyeClientAsync(fixture, 12, "NoReCo");
        Guid cash = await CreateAccountAsync(http, clientId, "1000", "Cash", "Asset");
        Guid revenue = await CreateAccountAsync(http, clientId, "4000", "Revenue", "Revenue");
        // Temporary-account activity exists, but NO retained-earnings account is designated.
        await PostAndApproveAsync(http, clientId, new DateOnly(2024, 12, 31), cash, revenue, 100m);

        await AssertProblemAsync(await CloseYearAsync(http, clientId, new DateOnly(2024, 12, 31)),
            HttpStatusCode.Conflict, "retained-earnings account");
    }
}
