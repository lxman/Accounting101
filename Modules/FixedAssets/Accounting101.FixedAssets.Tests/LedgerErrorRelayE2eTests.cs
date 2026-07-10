using System.Net;
using System.Net.Http.Json;
using Accounting101.FixedAssets.Api;
using Accounting101.Ledger.Contracts;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Accounting101.FixedAssets.Tests;

/// <summary>A fold-on-read refusal is relayed with the engine's real status (a 4xx), not an opaque 500.
/// Exercised via the accumulated-depreciation fold: the Accumulated Depreciation account is configured
/// WITHOUT the "Asset" required dimension, so the engine's subledger validation refuses the fold read
/// (422, LedgerEndpoints.cs:554). The list and detail asset reads must relay that 422, not let the bare
/// EnsureSuccessStatusCode 500 escape — the exact /assets smoke crash, pinned.</summary>
public sealed class LedgerErrorRelayE2eTests(FixedAssetsHostFixture fixture) : IClassFixture<FixedAssetsHostFixture>
{
    [Fact]
    public async Task Listing_assets_when_the_accum_account_lacks_its_dimension_relays_the_ledger_status_not_500()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpMisconfiguredChartAsync(http, clientId);
        await CreateAssetAsync(http, clientId);

        HttpResponseMessage resp = await http.GetAsync($"/clients/{clientId}/assets");

        await AssertRelayed422(resp);
    }

    [Fact]
    public async Task Getting_one_asset_when_the_accum_account_lacks_its_dimension_relays_the_ledger_status_not_500()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpMisconfiguredChartAsync(http, clientId);
        Guid assetId = await CreateAssetAsync(http, clientId);

        HttpResponseMessage resp = await http.GetAsync($"/clients/{clientId}/assets/{assetId}");

        await AssertRelayed422(resp);
    }

    private static async Task AssertRelayed422(HttpResponseMessage resp)
    {
        Assert.NotEqual(HttpStatusCode.InternalServerError, resp.StatusCode);
        Assert.InRange((int)resp.StatusCode, 400, 499);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        ProblemDetails? problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.False(string.IsNullOrWhiteSpace(problem!.Detail));
    }

    /// <summary>Every posting account is configured, but Accumulated Depreciation is a PLAIN Asset account
    /// (no RequiredDimensions), so the "Asset"-dimensioned fold read refuses with 422.</summary>
    private async Task SetUpMisconfiguredChartAsync(HttpClient http, Guid clientId)
    {
        await PutAccountAsync(http, clientId, fixture.DepreciationExpenseAccountId,     "6200", "Depreciation Expense",     "Expense");
        await PutAccountAsync(http, clientId, fixture.AccumulatedDepreciationAccountId, "1590", "Accumulated Depreciation", "Asset"); // NO RequiredDimensions — the misconfig
        await PutAccountAsync(http, clientId, fixture.AssetCostAccountId,        "1500", "Fixed Assets",     "Asset");
        await PutAccountAsync(http, clientId, fixture.DisposalProceedsAccountId, "1000", "Cash",             "Asset");
        await PutAccountAsync(http, clientId, fixture.GainOnDisposalAccountId,   "7100", "Gain on Disposal", "Revenue");
        await PutAccountAsync(http, clientId, fixture.LossOnDisposalAccountId,   "7200", "Loss on Disposal", "Expense");
    }

    private static async Task PutAccountAsync(HttpClient http, Guid clientId, Guid accountId, string number, string name, string type) =>
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{accountId}",
            new AccountRequest { Number = number, Name = name, Type = type })).EnsureSuccessStatusCode();

    private static async Task<Guid> CreateAssetAsync(HttpClient http, Guid clientId)
    {
        AssetView view = (await (await http.PostAsJsonAsync($"/clients/{clientId}/assets",
            new SaveAssetRequest("Van", 12000m, new DateOnly(2026, 1, 1), 24, 0m, DepreciationMethod.StraightLine, null)))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<AssetView>())!;
        return view.Asset.Id;
    }
}
