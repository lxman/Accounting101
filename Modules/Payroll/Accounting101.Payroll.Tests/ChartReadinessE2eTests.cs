using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;
using Accounting101.ModuleKit;
using Xunit;

namespace Accounting101.Payroll.Tests;

public sealed class ChartReadinessE2eTests(PayrollHostFixture fixture) : IClassFixture<PayrollHostFixture>
{
    [Fact]
    public async Task Correct_chart_is_ready()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync(LedgerRole.Controller);
        await PutAccountAsync(http, clientId, fixture.SalariesExpenseAccountId, "6000", "Salaries Expense", "Expense");
        await PutAccountAsync(http, clientId, fixture.PayrollTaxExpenseAccountId, "6010", "Payroll Tax Expense", "Expense");
        await PutAccountAsync(http, clientId, fixture.CashAccountId, "1000", "Cash", "Asset");
        await PutAccountAsync(http, clientId, fixture.WithholdingsPayableAccountId, "2100", "Withholdings Payable", "Liability");
        await PutAccountAsync(http, clientId, fixture.PayrollTaxesPayableAccountId, "2110", "Payroll Taxes Payable", "Liability");

        ChartReadinessReport report = (await http.GetFromJsonAsync<ChartReadinessReport>(
            $"/clients/{clientId}/payroll/chart-readiness"))!;

        Assert.Equal("payroll", report.ModuleKey);
        Assert.True(report.Ready);
        Assert.All(report.Accounts, a => Assert.Equal(AccountReadinessStatus.Ok, a.Status));
    }

    [Fact]
    public async Task Chart_missing_the_withholdings_payable_account_is_not_ready()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync(LedgerRole.Controller);
        await PutAccountAsync(http, clientId, fixture.SalariesExpenseAccountId, "6000", "Salaries Expense", "Expense");
        await PutAccountAsync(http, clientId, fixture.PayrollTaxExpenseAccountId, "6010", "Payroll Tax Expense", "Expense");
        await PutAccountAsync(http, clientId, fixture.CashAccountId, "1000", "Cash", "Asset");
        // Deliberately not seeding WithholdingsPayable.
        await PutAccountAsync(http, clientId, fixture.PayrollTaxesPayableAccountId, "2110", "Payroll Taxes Payable", "Liability");

        HttpResponseMessage resp = await http.GetAsync($"/clients/{clientId}/payroll/chart-readiness");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode); // advisory — 200 even when not ready
        ChartReadinessReport report = (await resp.Content.ReadFromJsonAsync<ChartReadinessReport>())!;

        Assert.False(report.Ready);
        AccountReadinessResult withholdings = Assert.Single(report.Accounts,
            a => a.AccountId == fixture.WithholdingsPayableAccountId);
        Assert.Equal(AccountReadinessStatus.Missing, withholdings.Status);
    }

    private static async Task PutAccountAsync(HttpClient http, Guid clientId, Guid accountId,
        string number, string name, string type) =>
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{accountId}",
            new AccountRequest { Number = number, Name = name, Type = type }))
            .EnsureSuccessStatusCode();
}
