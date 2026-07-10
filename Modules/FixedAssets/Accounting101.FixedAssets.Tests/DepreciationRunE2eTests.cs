using System.Net;
using System.Net.Http.Json;
using Accounting101.FixedAssets.Api;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets.Tests;

/// <summary>Proves depreciation runs end-to-end through the real host: a run advances each asset's
/// accumulated depreciation and posts one balanced PendingApproval entry stamped ViaModule="fixedassets";
/// the period guard and nothing-to-depreciate rules hold; a LIFO void reverses the entry and rolls
/// accumulated depreciation back.</summary>
public sealed class DepreciationRunE2eTests(FixedAssetsHostFixture fixture) : IClassFixture<FixedAssetsHostFixture>
{
    private async Task SetUpChartAsync(HttpClient http, Guid clientId)
    {
        await PutAccountAsync(http, clientId, fixture.DepreciationExpenseAccountId,     "6200", "Depreciation Expense",     "Expense");
        await PutAccountAsync(http, clientId, fixture.AccumulatedDepreciationAccountId, "1590", "Accumulated Depreciation", "Asset",
            requiredDimensions: ["Asset"]);
    }

    private static async Task PutAccountAsync(HttpClient http, Guid clientId, Guid accountId, string number, string name, string type,
        IReadOnlyList<string>? requiredDimensions = null) =>
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{accountId}",
            new AccountRequest { Number = number, Name = name, Type = type, RequiredDimensions = requiredDimensions }))
            .EnsureSuccessStatusCode();

    private static async Task<AssetView> CreateAssetAsync(HttpClient http, Guid clientId, SaveAssetRequest req) =>
        (await (await http.PostAsJsonAsync($"/clients/{clientId}/assets", req)).EnsureSuccessStatusCode()
            .Content.ReadFromJsonAsync<AssetView>())!;

    [Fact]
    public async Task Run_advances_accumulated_and_posts_one_pending_entry_via_fixedassets()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync(); // Controller (holds fixedassets.write)
        await SetUpChartAsync(http, clientId);

        AssetView sl = await CreateAssetAsync(http, clientId,
            new SaveAssetRequest("SL Van", 12000m, new DateOnly(2026, 1, 1), 24, 0m, DepreciationMethod.StraightLine, null)); // 500/mo
        AssetView db = await CreateAssetAsync(http, clientId,
            new SaveAssetRequest("DB Rig", 12000m, new DateOnly(2026, 1, 1), 24, 0m, DepreciationMethod.DecliningBalance, 2.0m)); // 1000 first mo

        HttpResponseMessage created = await http.PostAsJsonAsync($"/clients/{clientId}/depreciation-runs",
            new RunDepreciationRequest(2026, 1, null, "January depreciation"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        DepreciationRunView run = (await created.Content.ReadFromJsonAsync<DepreciationRunView>())!;
        Assert.Equal(1500m, run.Run.Total); // 500 + 1000

        // One balanced PendingApproval entry via fixedassets.
        EntryResponse[] entries = (await http.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={run.Run.Id}"))!;
        EntryResponse entry = Assert.Single(entries);
        Assert.Equal("fixedassets", entry.ViaModule);
        Assert.Equal("PendingApproval", entry.Posting);
        Assert.Equal(3, entry.Lines.Count); // 1 aggregate expense debit + 2 per-asset accum credits
        Assert.Equal(1500m, entry.Lines.Single(l => l.AccountId == fixture.DepreciationExpenseAccountId && l.Direction == "Debit").Amount);
        Assert.Equal(500m, entry.Lines.Single(l =>
            l.AccountId == fixture.AccumulatedDepreciationAccountId && l.Direction == "Credit" && l.Dimensions["Asset"] == sl.Asset.Id).Amount);
        Assert.Equal(1000m, entry.Lines.Single(l =>
            l.AccountId == fixture.AccumulatedDepreciationAccountId && l.Direction == "Credit" && l.Dimensions["Asset"] == db.Asset.Id).Amount);

        // Reported accumulated depreciation folds the ledger Posted-only, so it reflects the run only once
        // the entry is approved (single-client seed: SoD not required, so the same Controller may approve).
        // Before approval the fold is 0; after approval it equals each asset's per-{Asset} accum credit.
        AssetView slPending = (await http.GetFromJsonAsync<AssetView>($"/clients/{clientId}/assets/{sl.Asset.Id}"))!;
        Assert.Equal(0m, slPending.Asset.AccumulatedDepreciation); // pending run not yet on the books

        (await http.PostAsync($"/clients/{clientId}/entries/{entry.Id}/approve", null)).EnsureSuccessStatusCode();

        AssetView slAfter = (await http.GetFromJsonAsync<AssetView>($"/clients/{clientId}/assets/{sl.Asset.Id}"))!;
        AssetView dbAfter = (await http.GetFromJsonAsync<AssetView>($"/clients/{clientId}/assets/{db.Asset.Id}"))!;
        Assert.Equal(500m, slAfter.Asset.AccumulatedDepreciation);
        Assert.Equal(1000m, dbAfter.Asset.AccumulatedDepreciation);
    }

    [Fact]
    public async Task List_endpoint_folds_accumulated_depreciation_per_asset_after_approval()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);

        AssetView sl = await CreateAssetAsync(http, clientId,
            new SaveAssetRequest("SL Van", 12000m, new DateOnly(2026, 1, 1), 24, 0m, DepreciationMethod.StraightLine, null)); // 500/mo
        AssetView db = await CreateAssetAsync(http, clientId,
            new SaveAssetRequest("DB Rig", 12000m, new DateOnly(2026, 1, 1), 24, 0m, DepreciationMethod.DecliningBalance, 2.0m)); // 1000 first mo

        DepreciationRunView run = (await (await http.PostAsJsonAsync($"/clients/{clientId}/depreciation-runs",
            new RunDepreciationRequest(2026, 1, null, "January depreciation"))).EnsureSuccessStatusCode()
            .Content.ReadFromJsonAsync<DepreciationRunView>())!;

        EntryResponse entry = Assert.Single((await http.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={run.Run.Id}"))!);

        // Before approval the Posted-only fold reads 0 on the LIST endpoint too (proves it goes through the fold,
        // not a stored default of 0 that would look identical here — the post-approval assertion is the real proof).
        PagedResponse<AssetView> pending = (await http.GetFromJsonAsync<PagedResponse<AssetView>>($"/clients/{clientId}/assets"))!;
        Assert.All(pending.Items, a => Assert.Equal(0m, a.Asset.AccumulatedDepreciation));

        (await http.PostAsync($"/clients/{clientId}/entries/{entry.Id}/approve", null)).EnsureSuccessStatusCode();

        // The stored field is gone; the LIST endpoint must overlay each asset's per-{Asset} accum from the ledger fold.
        PagedResponse<AssetView> listed = (await http.GetFromJsonAsync<PagedResponse<AssetView>>($"/clients/{clientId}/assets"))!;
        Assert.Equal(500m, listed.Items.Single(a => a.Asset.Id == sl.Asset.Id).Asset.AccumulatedDepreciation);
        Assert.Equal(1000m, listed.Items.Single(a => a.Asset.Id == db.Asset.Id).Asset.AccumulatedDepreciation);
    }

    /// <summary>Regression for the Task 5 review finding: PUT /assets/{id} must return the FOLDED
    /// accumulated depreciation, not the store's default of 0 (the stored field is gone). Runs and approves
    /// one month of depreciation, then edits a benign field (Description) and asserts the PUT response body
    /// itself carries the folded, non-zero accum — this fails if the handler echoes the store result instead
    /// of refetching via FixedAssetsService.GetAsync (mirrors ReactivateAsset's pattern).</summary>
    [Fact]
    public async Task Put_asset_response_folds_accumulated_depreciation_after_a_posted_run()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);

        AssetView sl = await CreateAssetAsync(http, clientId,
            new SaveAssetRequest("SL Van", 12000m, new DateOnly(2026, 1, 1), 24, 0m, DepreciationMethod.StraightLine, null)); // 500/mo

        DepreciationRunView run = (await (await http.PostAsJsonAsync($"/clients/{clientId}/depreciation-runs",
            new RunDepreciationRequest(2026, 1, null, "January depreciation"))).EnsureSuccessStatusCode()
            .Content.ReadFromJsonAsync<DepreciationRunView>())!;
        EntryResponse entry = Assert.Single((await http.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={run.Run.Id}"))!);
        (await http.PostAsync($"/clients/{clientId}/entries/{entry.Id}/approve", null)).EnsureSuccessStatusCode();

        // Sanity: the fold is non-zero on the books before we touch the PUT path.
        AssetView before = (await http.GetFromJsonAsync<AssetView>($"/clients/{clientId}/assets/{sl.Asset.Id}"))!;
        Assert.Equal(500m, before.Asset.AccumulatedDepreciation);

        HttpResponseMessage put = await http.PutAsJsonAsync($"/clients/{clientId}/assets/{sl.Asset.Id}",
            new SaveAssetRequest("SL Van (edited)", 12000m, new DateOnly(2026, 1, 1), 24, 0m, DepreciationMethod.StraightLine, null));
        put.EnsureSuccessStatusCode();
        AssetView updated = (await put.Content.ReadFromJsonAsync<AssetView>())!;
        Assert.Equal("SL Van (edited)", updated.Asset.Description);
        Assert.Equal(500m, updated.Asset.AccumulatedDepreciation); // NOT 0 — must be the folded value
    }

    [Fact]
    public async Task Second_run_for_the_same_period_returns_409()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);
        await CreateAssetAsync(http, clientId,
            new SaveAssetRequest("Van", 12000m, new DateOnly(2026, 1, 1), 24, 0m, DepreciationMethod.StraightLine, null));
        (await http.PostAsJsonAsync($"/clients/{clientId}/depreciation-runs", new RunDepreciationRequest(2026, 1, null, null)))
            .EnsureSuccessStatusCode();

        HttpResponseMessage second = await http.PostAsJsonAsync(
            $"/clients/{clientId}/depreciation-runs", new RunDepreciationRequest(2026, 1, null, null));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task A_period_with_no_eligible_assets_returns_422()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);
        // Asset goes into service after the run period.
        await CreateAssetAsync(http, clientId,
            new SaveAssetRequest("Van", 12000m, new DateOnly(2026, 6, 1), 24, 0m, DepreciationMethod.StraightLine, null));
        HttpResponseMessage run = await http.PostAsJsonAsync(
            $"/clients/{clientId}/depreciation-runs", new RunDepreciationRequest(2026, 1, null, null));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, run.StatusCode);
    }

    [Fact]
    public async Task Void_latest_run_reverses_entry_and_rolls_back_accumulated()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);
        AssetView sl = await CreateAssetAsync(http, clientId,
            new SaveAssetRequest("Van", 12000m, new DateOnly(2026, 1, 1), 24, 0m, DepreciationMethod.StraightLine, null));

        DepreciationRunView run = (await (await http.PostAsJsonAsync($"/clients/{clientId}/depreciation-runs",
            new RunDepreciationRequest(2026, 1, null, null))).EnsureSuccessStatusCode()
            .Content.ReadFromJsonAsync<DepreciationRunView>())!;

        HttpResponseMessage voided = await http.PostAsJsonAsync(
            $"/clients/{clientId}/depreciation-runs/{run.Run.Id}/void", new VoidReasonRequest("entered in error"));
        voided.EnsureSuccessStatusCode();
        DepreciationRunView voidedRun = (await voided.Content.ReadFromJsonAsync<DepreciationRunView>())!;
        Assert.Equal(DepreciationRunStatus.Voided, voidedRun.Run.Status);

        // Accumulated rolled back.
        AssetView after = (await http.GetFromJsonAsync<AssetView>($"/clients/{clientId}/assets/{sl.Asset.Id}"))!;
        Assert.Equal(0m, after.Asset.AccumulatedDepreciation);

        // Entry voided/reversed on the books.
        EntryResponse[] entries = (await http.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={run.Run.Id}"))!;
        // A pending entry is withdrawn (single, Voided); a posted one leaves the original + a reversal.
        Assert.Contains(entries, e => e.Status == "Voided" || e.ReversalOf != null);
    }

    [Fact]
    public async Task Voiding_a_non_latest_run_returns_409()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);
        await CreateAssetAsync(http, clientId,
            new SaveAssetRequest("Van", 12000m, new DateOnly(2026, 1, 1), 24, 0m, DepreciationMethod.StraightLine, null));
        DepreciationRunView jan = (await (await http.PostAsJsonAsync($"/clients/{clientId}/depreciation-runs",
            new RunDepreciationRequest(2026, 1, null, null))).EnsureSuccessStatusCode()
            .Content.ReadFromJsonAsync<DepreciationRunView>())!;
        (await http.PostAsJsonAsync($"/clients/{clientId}/depreciation-runs", new RunDepreciationRequest(2026, 2, null, null)))
            .EnsureSuccessStatusCode();

        HttpResponseMessage voidJan = await http.PostAsJsonAsync(
            $"/clients/{clientId}/depreciation-runs/{jan.Run.Id}/void", new VoidReasonRequest(null));
        Assert.Equal(HttpStatusCode.Conflict, voidJan.StatusCode);
    }

    /// <summary>Capability denial (not a wrong-client denial): the Auditor is seeded on the SAME client
    /// where the chart/asset live, holding fixedassets.read but not fixedassets.write — mirrors FA-1's
    /// <c>A_member_without_fixedassets_write_cannot_create_but_can_read</c>.</summary>
    [Fact]
    public async Task A_member_without_write_cannot_run_depreciation()
    {
        (Guid clientId, HttpClient controller) = await fixture.SeedClientAsync(); // Controller sets up chart
        await SetUpChartAsync(controller, clientId);
        await CreateAssetAsync(controller, clientId,
            new SaveAssetRequest("Van", 12000m, new DateOnly(2026, 1, 1), 24, 0m, DepreciationMethod.StraightLine, null));

        // An Auditor (read-only) on the SAME client attempts a run.
        Guid auditorUserId = Guid.NewGuid();
        await fixture.Control().AddMembershipAsync(auditorUserId, clientId, LedgerRole.Auditor);
        HttpClient auditor = fixture.ClientFor(auditorUserId, "Acme Auditor", LedgerRole.Auditor);

        HttpResponseMessage run = await auditor.PostAsJsonAsync(
            $"/clients/{clientId}/depreciation-runs", new RunDepreciationRequest(2026, 1, null, null));
        Assert.Equal(HttpStatusCode.Forbidden, run.StatusCode);
    }

    [Fact]
    public async Task A_client_not_entitled_to_fixedassets_is_forbidden()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync(enabledModules: []); // no fixedassets entitlement
        HttpResponseMessage run = await http.PostAsJsonAsync(
            $"/clients/{clientId}/depreciation-runs", new RunDepreciationRequest(2026, 1, null, null));
        Assert.Equal(HttpStatusCode.Forbidden, run.StatusCode);
    }

    /// <summary>Proves the Task 3 enforcement flip: once Accumulated Depreciation requires the Asset
    /// dimension, an untagged (unfoldable) accum line becomes structurally impossible — the engine
    /// rejects it at post, 422, naming the missing dimension. This bypasses the depreciation-run recipe
    /// entirely and posts a hand-built entry directly, so it proves the engine-level guarantee independent
    /// of the recipe's own (already-correct, per Task 2) dimensioning.</summary>
    [Fact]
    public async Task Accumulated_depreciation_account_rejects_an_untagged_line()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId); // sets RequiredDimensions=["Asset"] on the accum account

        PostEntryRequest e = new(null, new DateOnly(2026, 6, 30), "R", "m",
        [
            new PostLineRequest(fixture.DepreciationExpenseAccountId, "Debit", 100m),
            new PostLineRequest(fixture.AccumulatedDepreciationAccountId, "Credit", 100m), // no Asset
        ]);
        HttpResponseMessage r = await http.PostAsJsonAsync($"/clients/{clientId}/entries", e);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Contains("Asset", await r.Content.ReadAsStringAsync());
    }
}
