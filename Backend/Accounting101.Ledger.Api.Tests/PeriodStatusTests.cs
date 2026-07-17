using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>The period-status read endpoint: closed-through is null before any close and the closed date
/// after one; the fiscal-year-end month is reported; the endpoint requires membership (gl.read).</summary>
public sealed class PeriodStatusTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Reports_null_closed_through_and_the_fiscal_month_before_any_close()
    {
        SeededClient c = await fixture.SeedClientAsync();

        PeriodStatusResponse resp = (await c.Http.GetFromJsonAsync<PeriodStatusResponse>(
            $"/clients/{c.ClientId}/periods/status"))!;

        Assert.Null(resp.ClosedThrough);
        Assert.Equal(12, resp.FiscalYearEndMonth); // seed leaves FiscalYearEndMonth unset → FiscalYear.MonthOf → 12
    }

    [Fact]
    public async Task Reports_the_closed_through_date_after_a_close()
    {
        SeededClient c = await fixture.SeedClientAsync();
        DateOnly asOf = new(2026, 3, 31);
        (await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/periods/close", new ClosePeriodRequest(asOf)))
            .EnsureSuccessStatusCode();

        PeriodStatusResponse resp = (await c.Http.GetFromJsonAsync<PeriodStatusResponse>(
            $"/clients/{c.ClientId}/periods/status"))!;

        Assert.Equal(asOf, resp.ClosedThrough);
    }

    [Fact]
    public async Task Requires_membership()
    {
        SeededClient c = await fixture.SeedClientAsync();                       // Controller: member, has gl.read
        HttpClient stranger = fixture.ClientFor(Guid.NewGuid(), "Stranger", ("role", "Controller")); // not a member

        Assert.Equal(HttpStatusCode.Forbidden,
            (await stranger.GetAsync($"/clients/{c.ClientId}/periods/status")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await c.Http.GetAsync($"/clients/{c.ClientId}/periods/status")).StatusCode);
    }
}
