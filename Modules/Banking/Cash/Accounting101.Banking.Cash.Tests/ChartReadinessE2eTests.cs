using System.Net;
using System.Net.Http.Json;
using Accounting101.Banking.Cash.Api;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;
using Accounting101.ModuleKit;
using Xunit;

namespace Accounting101.Banking.Cash.Tests;

public sealed class ChartReadinessE2eTests(CashHostFixture fixture) : IClassFixture<CashHostFixture>
{
    [Fact]
    public async Task Correct_chart_is_ready()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync(LedgerRole.Controller);
        await PutAccountAsync(http, clientId, fixture.CashAccountId, "1000", "Cash", "Asset");

        ChartReadinessReport report = (await http.GetFromJsonAsync<ChartReadinessReport>(
            $"/clients/{clientId}/cash/chart-readiness"))!;

        Assert.Equal("cash", report.ModuleKey);
        Assert.True(report.Ready);
        Assert.All(report.Accounts, a => Assert.Equal(AccountReadinessStatus.Ok, a.Status));
    }

    [Fact]
    public async Task Chart_missing_the_cash_account_is_not_ready()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync(LedgerRole.Controller);
        // Deliberately not seeding the cash account.

        HttpResponseMessage resp = await http.GetAsync($"/clients/{clientId}/cash/chart-readiness");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode); // advisory — 200 even when not ready
        ChartReadinessReport report = (await resp.Content.ReadFromJsonAsync<ChartReadinessReport>())!;

        Assert.False(report.Ready);
        AccountReadinessResult cash = Assert.Single(report.Accounts,
            a => a.AccountId == fixture.CashAccountId);
        Assert.Equal(AccountReadinessStatus.Missing, cash.Status);
    }

    private static async Task PutAccountAsync(HttpClient http, Guid clientId, Guid accountId,
        string number, string name, string type) =>
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{accountId}",
            new AccountRequest { Number = number, Name = name, Type = type }))
            .EnsureSuccessStatusCode();
}
