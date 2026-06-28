using System.Net;
using static Accounting101.Ledger.Api.Tests.TemporalScenario;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>Fiscal-year-end guards for a FEBRUARY-fiscal-year client — exercises the leap-aware
/// FiscalYear.EndDateFor end-to-end (2024-02-29 leap, 2025-02-28 non-leap), extending the Dec/June
/// coverage in FiscalYearCloseGuardTests.</summary>
public sealed class FiscalYearBoundaryE2eTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private async Task<(Guid clientId, HttpClient http, Guid cash, Guid revenue)> ArrangeFebClientAsync(string name)
    {
        (Guid clientId, HttpClient http) = await SeedFyeClientAsync(fixture, 2, name);
        Guid cash = await CreateAccountAsync(http, clientId, "1000", "Cash", "Asset");
        Guid revenue = await CreateAccountAsync(http, clientId, "4000", "Revenue", "Revenue");
        await CreateAccountAsync(http, clientId, "3900", "Retained Earnings", "Equity", retained: true);
        return (clientId, http, cash, revenue);
    }

    [Fact]
    public async Task Close_year_on_the_leap_day_fiscal_year_end_succeeds()
    {
        (Guid clientId, HttpClient http, Guid cash, Guid revenue) = await ArrangeFebClientAsync("FebCo-Leap");
        await PostAndApproveAsync(http, clientId, new DateOnly(2024, 2, 29), cash, revenue, 100m);

        Assert.Equal(HttpStatusCode.OK, (await CloseYearAsync(http, clientId, new DateOnly(2024, 2, 29))).StatusCode);
    }

    [Fact]
    public async Task Monthly_close_on_the_leap_day_fiscal_year_end_is_refused()
    {
        (Guid clientId, HttpClient http, Guid cash, Guid revenue) = await ArrangeFebClientAsync("FebCo-LeapBlock");
        await PostAndApproveAsync(http, clientId, new DateOnly(2024, 2, 29), cash, revenue, 100m);

        await AssertProblemAsync(await CloseAsync(http, clientId, new DateOnly(2024, 2, 29)),
            HttpStatusCode.Conflict, "close-year");
    }

    [Fact]
    public async Task Close_year_on_the_non_leap_year_end_succeeds()
    {
        (Guid clientId, HttpClient http, Guid cash, Guid revenue) = await ArrangeFebClientAsync("FebCo-NonLeap");
        // FY2025 ends 2025-02-28 (non-leap). Activity dated on that date, then close-year it.
        await PostAndApproveAsync(http, clientId, new DateOnly(2025, 2, 28), cash, revenue, 100m);

        Assert.Equal(HttpStatusCode.OK, (await CloseYearAsync(http, clientId, new DateOnly(2025, 2, 28))).StatusCode);
    }

    [Fact]
    public async Task Close_year_on_a_wrong_date_names_the_february_fiscal_year_end()
    {
        (Guid clientId, HttpClient http, _, _) = await ArrangeFebClientAsync("FebCo-WrongDate");

        // 2024-06-30 is not the Feb client's FY-end; the guard names the real one (2024-02-29).
        await AssertProblemAsync(await CloseYearAsync(http, clientId, new DateOnly(2024, 6, 30)),
            HttpStatusCode.Conflict, "2024-02-29");
    }
}
