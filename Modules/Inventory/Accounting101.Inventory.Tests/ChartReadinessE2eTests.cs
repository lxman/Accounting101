using System.Net;
using System.Net.Http.Json;
using Accounting101.Inventory.Api;
using Accounting101.Ledger.Contracts;
using Accounting101.ModuleKit;
using Xunit;

namespace Accounting101.Inventory.Tests;

public sealed class ChartReadinessE2eTests(InventoryHostFixture fixture) : IClassFixture<InventoryHostFixture>
{
    [Fact]
    public async Task Correct_chart_is_ready()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId, inventoryAssetDims: ["Item"]);

        ChartReadinessReport report = (await http.GetFromJsonAsync<ChartReadinessReport>(
            $"/clients/{clientId}/inventory/chart-readiness"))!;

        Assert.Equal("inventory", report.ModuleKey);
        Assert.True(report.Ready);
        Assert.All(report.Accounts, a => Assert.Equal(AccountReadinessStatus.Ok, a.Status));
    }

    [Fact]
    public async Task Inventory_asset_account_without_item_dimension_is_not_ready()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId, inventoryAssetDims: null); // misconfig

        HttpResponseMessage resp = await http.GetAsync($"/clients/{clientId}/inventory/chart-readiness");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode); // advisory — 200 even when not ready
        ChartReadinessReport report = (await resp.Content.ReadFromJsonAsync<ChartReadinessReport>())!;

        Assert.False(report.Ready);
        AccountReadinessResult asset = Assert.Single(report.Accounts,
            a => a.AccountId == fixture.InventoryAssetAccountId);
        Assert.Equal(AccountReadinessStatus.MissingDimensions, asset.Status);
    }

    private async Task SetUpChartAsync(HttpClient http, Guid clientId, IReadOnlyList<string>? inventoryAssetDims)
    {
        await Put(http, clientId, fixture.InventoryAssetAccountId,      "1400", "Inventory Asset",     "Asset",     inventoryAssetDims);
        await Put(http, clientId, fixture.CogsAccountId,                "5000", "Cost of Goods Sold",  "Expense",   null);
        await Put(http, clientId, fixture.GrniClearingAccountId,        "2100", "GRNI Clearing",       "Liability", null);
        await Put(http, clientId, fixture.InventoryAdjustmentAccountId, "5100", "Inventory Adjustment","Expense",   null);
    }

    private static async Task Put(HttpClient http, Guid clientId, Guid id, string number, string name, string type,
        IReadOnlyList<string>? dims) =>
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{id}",
            new AccountRequest { Number = number, Name = name, Type = type, RequiredDimensions = dims }))
            .EnsureSuccessStatusCode();
}
