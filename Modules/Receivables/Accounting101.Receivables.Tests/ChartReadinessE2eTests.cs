using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;
using Accounting101.ModuleKit;
using Accounting101.Receivables.Api;
using Xunit;

namespace Accounting101.Receivables.Tests;

/// <summary>Advisory chart-readiness for receivables — the most complex of the six modules, since it draws
/// accounts from BOTH the invoice and payment posting-account bags and must dedupe the shared Receivable
/// account (declared once by <see cref="ReceivablesChartRequirements"/>, from the invoice bag).</summary>
public sealed class ChartReadinessE2eTests(ReceivablesHostFixture fixture) : IClassFixture<ReceivablesHostFixture>
{
    [Fact]
    public async Task Correct_chart_is_ready()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId, receivableDims: ["Customer", "Invoice"]);

        ChartReadinessReport report = (await http.GetFromJsonAsync<ChartReadinessReport>(
            $"/clients/{clientId}/receivables/chart-readiness"))!;

        Assert.Equal("receivables", report.ModuleKey);
        Assert.True(report.Ready);
        Assert.All(report.Accounts, a => Assert.Equal(AccountReadinessStatus.Ok, a.Status));

        AccountReadinessResult customerCredits = Assert.Single(report.Accounts,
            a => a.AccountId == fixture.CustomerCreditsAccountId);
        Assert.Equal(AccountReadinessStatus.Ok, customerCredits.Status);
        Assert.Contains("Customer", customerCredits.RequiredDimensions);
    }

    [Fact]
    public async Task Receivable_account_without_customer_and_invoice_dimensions_is_not_ready()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId, receivableDims: null); // misconfig — A/R missing its fold dimensions

        HttpResponseMessage resp = await http.GetAsync($"/clients/{clientId}/receivables/chart-readiness");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode); // advisory — 200 even when not ready
        ChartReadinessReport report = (await resp.Content.ReadFromJsonAsync<ChartReadinessReport>())!;

        Assert.False(report.Ready);
        AccountReadinessResult receivable = Assert.Single(report.Accounts,
            a => a.AccountId == fixture.ReceivableAccountId);
        Assert.Equal(AccountReadinessStatus.MissingDimensions, receivable.Status);
    }

    /// <summary>Mirrors the AR proof/relay chart-setup (<c>SettlementScenario.SetUpChartAsync</c>), except
    /// the Receivable account's dimensions are parameterized so the misconfigured case can drop them.</summary>
    private async Task SetUpChartAsync(HttpClient http, Guid clientId, IReadOnlyList<string>? receivableDims)
    {
        await Put(http, clientId, fixture.ReceivableAccountId,      "1100", "Accounts Receivable", "Asset",     receivableDims);
        await Put(http, clientId, fixture.CustomerCreditsAccountId, "2300", "Customer Credits",    "Liability", ["Customer"]);
        await Put(http, clientId, fixture.RevenueAccountId,         "4000", "Revenue",             "Revenue",   null);
        await Put(http, clientId, fixture.SalesTaxPayableAccountId, "2200", "Sales Tax Payable",   "Liability", null);
        await Put(http, clientId, fixture.CashAccountId,            "1000", "Cash",                "Asset",     null);
        await Put(http, clientId, fixture.BadDebtExpenseAccountId,  "6000", "Bad Debt Expense",    "Expense",   null);
        await Put(http, clientId, fixture.SalesReturnsAccountId,    "4900", "Sales Returns",       "Revenue",   null);
    }

    private static async Task Put(HttpClient http, Guid clientId, Guid id, string number, string name, string type,
        IReadOnlyList<string>? dims) =>
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{id}",
            new AccountRequest { Number = number, Name = name, Type = type, RequiredDimensions = dims }))
            .EnsureSuccessStatusCode();
}
