using System.Net;
using static Accounting101.Ledger.Api.Tests.TemporalScenario;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>A two-year fiscal cycle: each year-end close zeros the temporary accounts and rolls net income
/// into retained earnings, and retained earnings ACCUMULATES across both years. The books balance at each
/// year-end.</summary>
public sealed class FiscalCycleIntegrityE2eTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Retained_earnings_accumulates_and_temporaries_re_zero_across_two_year_ends()
    {
        (Guid clientId, HttpClient http) = await SeedFyeClientAsync(fixture, 12, "CycleCo");
        Guid cash = await CreateAccountAsync(http, clientId, "1000", "Cash", "Asset");
        Guid revenue = await CreateAccountAsync(http, clientId, "4000", "Revenue", "Revenue");
        Guid expense = await CreateAccountAsync(http, clientId, "5000", "Expense", "Expense");
        Guid retained = await CreateAccountAsync(http, clientId, "3900", "Retained Earnings", "Equity", retained: true);

        // FY2024: revenue 1000, expense 600 → net income 400.
        await PostAndApproveAsync(http, clientId, new DateOnly(2024, 6, 15), cash, revenue, 1000m);
        await PostAndApproveAsync(http, clientId, new DateOnly(2024, 6, 20), expense, cash, 600m);
        Assert.Equal(HttpStatusCode.OK, (await CloseYearAsync(http, clientId, new DateOnly(2024, 12, 31))).StatusCode);

        DateOnly ye1 = new(2024, 12, 31);
        Assert.Equal(0m, await AccountBalanceAsync(http, clientId, revenue, ye1));    // temporary zeroed
        Assert.Equal(0m, await AccountBalanceAsync(http, clientId, expense, ye1));    // temporary zeroed
        Assert.Equal(-400m, await AccountBalanceAsync(http, clientId, retained, ye1)); // net income #1 (credit)
        await AssertBalancedAsync(http, clientId, ye1);

        // FY2025: revenue 800, expense 300 → net income 500.
        await PostAndApproveAsync(http, clientId, new DateOnly(2025, 6, 15), cash, revenue, 800m);
        await PostAndApproveAsync(http, clientId, new DateOnly(2025, 6, 20), expense, cash, 300m);
        Assert.Equal(HttpStatusCode.OK, (await CloseYearAsync(http, clientId, new DateOnly(2025, 12, 31))).StatusCode);

        DateOnly ye2 = new(2025, 12, 31);
        Assert.Equal(0m, await AccountBalanceAsync(http, clientId, revenue, ye2));    // re-zeroed
        Assert.Equal(0m, await AccountBalanceAsync(http, clientId, expense, ye2));    // re-zeroed
        Assert.Equal(-900m, await AccountBalanceAsync(http, clientId, retained, ye2)); // ACCUMULATED (−400 − 500)
        await AssertBalancedAsync(http, clientId, ye2);
    }
}
