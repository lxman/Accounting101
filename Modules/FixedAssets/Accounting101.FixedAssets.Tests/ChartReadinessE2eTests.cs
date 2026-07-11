using System.Net;
using System.Net.Http.Json;
using Accounting101.FixedAssets.Api;
using Accounting101.Ledger.Contracts;
using Accounting101.ModuleKit;
using Xunit;

namespace Accounting101.FixedAssets.Tests;

public sealed class ChartReadinessE2eTests(FixedAssetsHostFixture fixture) : IClassFixture<FixedAssetsHostFixture>
{
    [Fact]
    public async Task Correct_chart_is_ready()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId, accumDims: ["Asset"]);

        ChartReadinessReport report = (await http.GetFromJsonAsync<ChartReadinessReport>(
            $"/clients/{clientId}/fixedassets/chart-readiness"))!;

        Assert.Equal("fixedassets", report.ModuleKey);
        Assert.True(report.Ready);
        Assert.All(report.Accounts, a => Assert.Equal(AccountReadinessStatus.Ok, a.Status));
    }

    [Fact]
    public async Task Accum_account_without_asset_dimension_is_not_ready()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId, accumDims: null); // misconfig

        HttpResponseMessage resp = await http.GetAsync($"/clients/{clientId}/fixedassets/chart-readiness");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode); // advisory — 200 even when not ready
        ChartReadinessReport report = (await resp.Content.ReadFromJsonAsync<ChartReadinessReport>())!;

        Assert.False(report.Ready);
        AccountReadinessResult accum = Assert.Single(report.Accounts,
            a => a.AccountId == fixture.AccumulatedDepreciationAccountId);
        Assert.Equal(AccountReadinessStatus.MissingDimensions, accum.Status);
    }

    private async Task SetUpChartAsync(HttpClient http, Guid clientId, IReadOnlyList<string>? accumDims)
    {
        await Put(http, clientId, fixture.DepreciationExpenseAccountId,     "6200", "Depreciation Expense",     "Expense", null);
        await Put(http, clientId, fixture.AccumulatedDepreciationAccountId, "1590", "Accumulated Depreciation", "Asset",   accumDims);
        await Put(http, clientId, fixture.AssetCostAccountId,        "1500", "Fixed Assets",     "Asset",   null);
        await Put(http, clientId, fixture.DisposalProceedsAccountId, "1000", "Cash",             "Asset",   null);
        await Put(http, clientId, fixture.GainOnDisposalAccountId,   "7100", "Gain on Disposal", "Revenue", null);
        await Put(http, clientId, fixture.LossOnDisposalAccountId,   "7200", "Loss on Disposal", "Expense", null);
    }

    private static async Task Put(HttpClient http, Guid clientId, Guid id, string number, string name, string type,
        IReadOnlyList<string>? dims) =>
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{id}",
            new AccountRequest { Number = number, Name = name, Type = type, RequiredDimensions = dims }))
            .EnsureSuccessStatusCode();
}
