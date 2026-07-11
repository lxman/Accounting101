using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;
using Accounting101.ModuleKit;
using Accounting101.Payables.Api;
using Xunit;

namespace Accounting101.Payables.Tests;

/// <summary>Advisory chart-readiness for payables — a near-mirror of Receivables but simpler (one
/// provider method, 3 accounts). The one deliberate asymmetry: Vendor Credits is debit-normal
/// <c>Asset</c> (NOT <c>Liability</c> like AR's Customer Credits).</summary>
public sealed class ChartReadinessE2eTests(PayablesHostFixture fixture) : IClassFixture<PayablesHostFixture>
{
    [Fact]
    public async Task Correct_chart_is_ready()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId, payableDims: ["Vendor", "Bill"]);

        ChartReadinessReport report = (await http.GetFromJsonAsync<ChartReadinessReport>(
            $"/clients/{clientId}/payables/chart-readiness"))!;

        Assert.Equal("payables", report.ModuleKey);
        Assert.True(report.Ready);
        Assert.All(report.Accounts, a => Assert.Equal(AccountReadinessStatus.Ok, a.Status));

        AccountReadinessResult vendorCredits = Assert.Single(report.Accounts,
            a => a.AccountId == fixture.VendorCreditsAccountId);
        Assert.Equal(AccountReadinessStatus.Ok, vendorCredits.Status);
        Assert.Equal("Asset", vendorCredits.ExpectedType);
        Assert.Contains("Vendor", vendorCredits.RequiredDimensions);
    }

    [Fact]
    public async Task Payable_account_without_vendor_and_bill_dimensions_is_not_ready()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId, payableDims: null); // misconfig — A/P missing its fold dimensions

        HttpResponseMessage resp = await http.GetAsync($"/clients/{clientId}/payables/chart-readiness");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode); // advisory — 200 even when not ready
        ChartReadinessReport report = (await resp.Content.ReadFromJsonAsync<ChartReadinessReport>())!;

        Assert.False(report.Ready);
        AccountReadinessResult payable = Assert.Single(report.Accounts,
            a => a.AccountId == fixture.PayableAccountId);
        Assert.Equal(AccountReadinessStatus.MissingDimensions, payable.Status);
    }

    /// <summary>Mirrors the AP proof/relay chart-setup, except the Payable account's dimensions are
    /// parameterized so the misconfigured case can drop them.</summary>
    private async Task SetUpChartAsync(HttpClient http, Guid clientId, IReadOnlyList<string>? payableDims)
    {
        await Put(http, clientId, fixture.PayableAccountId,       "2000", "Accounts Payable", "Liability", payableDims);
        await Put(http, clientId, fixture.VendorCreditsAccountId, "1050", "Vendor Credits",    "Asset",     ["Vendor"]);
        await Put(http, clientId, fixture.CashAccountId,          "1000", "Cash",              "Asset",     null);
    }

    private static async Task Put(HttpClient http, Guid clientId, Guid id, string number, string name, string type,
        IReadOnlyList<string>? dims) =>
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{id}",
            new AccountRequest { Number = number, Name = name, Type = type, RequiredDimensions = dims }))
            .EnsureSuccessStatusCode();
}
