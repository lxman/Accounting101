using System.Net;
using System.Net.Http.Json;
using Accounting101.FixedAssets.Api;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets.Tests;

public sealed class FixedAssetsEndpointsTests(FixedAssetsHostFixture fixture) : IClassFixture<FixedAssetsHostFixture>
{
    private static SaveAssetRequest Van() => new(
        "Delivery van", 30000m, new DateOnly(2026, 1, 1), 60, 3000m, DepreciationMethod.StraightLine, null);

    /// <summary>Reads of an asset fold the Accumulated Depreciation subledger, so that account must exist as
    /// a control account requiring the Asset dimension before any single-asset GET / reactivate view. An
    /// asset with no depreciation folds to 0 — the account just has to be foldable.</summary>
    private async Task EnsureAccumAccountAsync(HttpClient http, Guid clientId) =>
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{fixture.AccumulatedDepreciationAccountId}",
            new AccountRequest { Number = "1590", Name = "Accumulated Depreciation", Type = "Asset", RequiredDimensions = ["Asset"] }))
            .EnsureSuccessStatusCode();

    [Fact]
    public async Task Create_list_get_update_deactivate_lifecycle()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await EnsureAccumAccountAsync(http, clientId);

        HttpResponseMessage created = await http.PostAsJsonAsync($"/clients/{clientId}/assets", Van());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        AssetView view = (await created.Content.ReadFromJsonAsync<AssetView>())!;
        Guid assetId = view.Asset.Id;
        Assert.Equal(AssetStatus.Active, view.Asset.Status);
        Assert.Equal(30000m, view.NetBookValue); // 30000 − 0 (new assets have zero accumulated depreciation)

        PagedResponse<AssetView> list = (await http.GetFromJsonAsync<PagedResponse<AssetView>>($"/clients/{clientId}/assets"))!;
        Assert.Contains(list.Items, a => a.Asset.Id == assetId);

        AssetView got = (await http.GetFromJsonAsync<AssetView>($"/clients/{clientId}/assets/{assetId}"))!;
        Assert.Equal("Delivery van", got.Asset.Description);

        HttpResponseMessage updated = await http.PutAsJsonAsync(
            $"/clients/{clientId}/assets/{assetId}", Van() with { Description = "Delivery van (renamed)", UsefulLifeMonths = 48 });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal("Delivery van (renamed)", (await updated.Content.ReadFromJsonAsync<AssetView>())!.Asset.Description);

        HttpResponseMessage deactivated = await http.PostAsync($"/clients/{clientId}/assets/{assetId}/deactivate", null);
        Assert.Equal(HttpStatusCode.NoContent, deactivated.StatusCode);

        PagedResponse<AssetView> afterList = (await http.GetFromJsonAsync<PagedResponse<AssetView>>($"/clients/{clientId}/assets"))!;
        Assert.DoesNotContain(afterList.Items, a => a.Asset.Id == assetId);
        PagedResponse<AssetView> withInactive = (await http.GetFromJsonAsync<PagedResponse<AssetView>>($"/clients/{clientId}/assets?includeInactive=true"))!;
        Assert.Contains(withInactive.Items, a => a.Asset.Id == assetId);
    }

    [Fact]
    public async Task Invalid_asset_is_422()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        HttpResponseMessage response = await http.PostAsJsonAsync(
            $"/clients/{clientId}/assets", Van() with { AcquisitionCost = 0m });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Deactivating_a_missing_asset_is_404_and_repeat_is_409()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        Assert.Equal(HttpStatusCode.NotFound,
            (await http.PostAsync($"/clients/{clientId}/assets/{Guid.NewGuid()}/deactivate", null)).StatusCode);

        AssetView created = (await (await http.PostAsJsonAsync($"/clients/{clientId}/assets", Van())).Content.ReadFromJsonAsync<AssetView>())!;
        Assert.Equal(HttpStatusCode.NoContent, (await http.PostAsync($"/clients/{clientId}/assets/{created.Asset.Id}/deactivate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsync($"/clients/{clientId}/assets/{created.Asset.Id}/deactivate", null)).StatusCode);
    }

    [Fact]
    public async Task A_member_without_fixedassets_write_cannot_create_but_can_read()
    {
        // Auditor holds fixedassets.read but NOT fixedassets.write.
        (Guid clientId, HttpClient auditor) = await fixture.SeedClientAsync(role: LedgerRole.Auditor);

        HttpResponseMessage create = await auditor.PostAsJsonAsync($"/clients/{clientId}/assets", Van());
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);

        HttpResponseMessage list = await auditor.GetAsync($"/clients/{clientId}/assets");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
    }

    [Fact]
    public async Task A_client_not_entitled_to_fixedassets_is_403()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync(enabledModules: []);
        HttpResponseMessage response = await http.PostAsJsonAsync($"/clients/{clientId}/assets", Van());
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Editing_a_deactivated_asset_returns_409_until_reactivated()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync(); // Controller by default
        await EnsureAccumAccountAsync(http, clientId);

        // Create + deactivate.
        AssetView created = (await (await http.PostAsJsonAsync($"/clients/{clientId}/assets",
            new SaveAssetRequest("Van", 12000m, new DateOnly(2026, 1, 1), 24, 0m, DepreciationMethod.StraightLine, null)))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<AssetView>())!;
        Guid assetId = created.Asset.Id;
        (await http.PostAsync($"/clients/{clientId}/assets/{assetId}/deactivate", null)).EnsureSuccessStatusCode();

        // Update now 409.
        HttpResponseMessage edit = await http.PutAsJsonAsync($"/clients/{clientId}/assets/{assetId}",
            new SaveAssetRequest("Van 2", 12000m, new DateOnly(2026, 1, 1), 24, 0m, DepreciationMethod.StraightLine, null));
        Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);

        // Reactivate, then update succeeds.
        (await http.PostAsync($"/clients/{clientId}/assets/{assetId}/reactivate", null)).EnsureSuccessStatusCode();
        HttpResponseMessage edit2 = await http.PutAsJsonAsync($"/clients/{clientId}/assets/{assetId}",
            new SaveAssetRequest("Van 2", 12000m, new DateOnly(2026, 1, 1), 24, 0m, DepreciationMethod.StraightLine, null));
        Assert.Equal(HttpStatusCode.OK, edit2.StatusCode);
    }

    [Fact]
    public async Task Reactivating_an_active_asset_returns_409()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        AssetView created = (await (await http.PostAsJsonAsync($"/clients/{clientId}/assets",
            new SaveAssetRequest("Van", 12000m, new DateOnly(2026, 1, 1), 24, 0m, DepreciationMethod.StraightLine, null)))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<AssetView>())!;
        HttpResponseMessage reactivate = await http.PostAsync($"/clients/{clientId}/assets/{created.Asset.Id}/reactivate", null);
        Assert.Equal(HttpStatusCode.Conflict, reactivate.StatusCode);
    }
}
