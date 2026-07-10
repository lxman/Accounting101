using System.Net;
using System.Net.Http.Json;
using Accounting101.FixedAssets.Api;
using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets.Tests;

/// <summary>Proves disposals end-to-end through the real host: a sale posts one balanced PendingApproval
/// entry via fixedassets, the asset goes Disposed with its final accumulated depreciation, disposed assets
/// are excluded from depreciation runs and frozen against edits, and a void reverses the entry and
/// reinstates the asset.</summary>
public sealed class DisposalE2eTests(FixedAssetsHostFixture fixture) : IClassFixture<FixedAssetsHostFixture>
{
    private async Task SetUpChartAsync(HttpClient http, Guid clientId)
    {
        await PutAccountAsync(http, clientId, fixture.DepreciationExpenseAccountId,     "6200", "Depreciation Expense",     "Expense");
        await PutAccountAsync(http, clientId, fixture.AccumulatedDepreciationAccountId, "1590", "Accumulated Depreciation", "Asset",
            requiredDimensions: ["Asset"]);
        await PutAccountAsync(http, clientId, fixture.AssetCostAccountId,               "1500", "Fixed Assets",             "Asset");
        await PutAccountAsync(http, clientId, fixture.DisposalProceedsAccountId,        "1000", "Cash",                     "Asset");
        await PutAccountAsync(http, clientId, fixture.GainOnDisposalAccountId,          "7100", "Gain on Disposal",         "Revenue");
        await PutAccountAsync(http, clientId, fixture.LossOnDisposalAccountId,          "7200", "Loss on Disposal",         "Expense");
    }

    private static async Task PutAccountAsync(HttpClient http, Guid clientId, Guid accountId, string number, string name, string type,
        IReadOnlyList<string>? requiredDimensions = null) =>
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{accountId}",
            new AccountRequest { Number = number, Name = name, Type = type, RequiredDimensions = requiredDimensions })).EnsureSuccessStatusCode();

    private static async Task<AssetView> CreateAssetAsync(HttpClient http, Guid clientId, SaveAssetRequest req) =>
        (await (await http.PostAsJsonAsync($"/clients/{clientId}/assets", req)).EnsureSuccessStatusCode()
            .Content.ReadFromJsonAsync<AssetView>())!;

    [Fact]
    public async Task Sale_disposes_the_asset_and_posts_one_balanced_pending_entry_via_fixedassets()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);
        AssetView asset = await CreateAssetAsync(http, clientId,
            new SaveAssetRequest("Van", 12000m, new DateOnly(2026, 1, 1), 24, 0m, DepreciationMethod.StraightLine, null));

        // Dispose Jun 2026: 5 months catch-up = 2500; NBV 9500; proceeds 10000 → gain 500.
        HttpResponseMessage created = await http.PostAsJsonAsync($"/clients/{clientId}/assets/{asset.Asset.Id}/dispose",
            new DisposeAssetRequest(new DateOnly(2026, 6, 30), 10000m, "sold"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        DisposalView disposal = (await created.Content.ReadFromJsonAsync<DisposalView>())!;
        Assert.Equal(500m, disposal.Disposal.GainLoss);
        // The final accumulated depreciation is captured on the evidentiary disposal doc.
        Assert.Equal(2500m, disposal.Disposal.AccumulatedAtDisposal);

        // Asset is Disposed; its reported accumulated depreciation folds the ledger, which the disposal
        // entry cleared (the {Asset} accum debit nets the fold to 0), so a disposed asset reports 0.
        AssetView after = (await http.GetFromJsonAsync<AssetView>($"/clients/{clientId}/assets/{asset.Asset.Id}"))!;
        Assert.Equal(AssetStatus.Disposed, after.Asset.Status);
        Assert.Equal(0m, after.Asset.AccumulatedDepreciation);

        // One balanced PendingApproval entry via fixedassets.
        EntryResponse[] entries = (await http.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={disposal.Disposal.Id}"))!;
        EntryResponse entry = Assert.Single(entries);
        Assert.Equal("fixedassets", entry.ViaModule);
        Assert.Equal("PendingApproval", entry.Posting);
        decimal debits = entry.Lines.Where(l => l.Direction == "Debit").Sum(l => l.Amount);
        decimal credits = entry.Lines.Where(l => l.Direction == "Credit").Sum(l => l.Amount);
        Assert.Equal(debits, credits);
        Assert.Equal(500m, entry.Lines.Single(l => l.AccountId == fixture.GainOnDisposalAccountId && l.Direction == "Credit").Amount);
    }

    [Fact]
    public async Task Retirement_with_zero_proceeds_posts_a_loss()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);
        AssetView asset = await CreateAssetAsync(http, clientId,
            new SaveAssetRequest("Rig", 12000m, new DateOnly(2026, 1, 1), 24, 0m, DepreciationMethod.StraightLine, null));

        // Dispose same in-service month → 0 catch-up, NBV 12000, proceeds 0 → loss 12000.
        HttpResponseMessage created = await http.PostAsJsonAsync($"/clients/{clientId}/assets/{asset.Asset.Id}/dispose",
            new DisposeAssetRequest(new DateOnly(2026, 1, 15), 0m, "scrapped"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        DisposalView disposal = (await created.Content.ReadFromJsonAsync<DisposalView>())!;
        Assert.Equal(-12000m, disposal.Disposal.GainLoss);

        EntryResponse[] entries = (await http.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={disposal.Disposal.Id}"))!;
        EntryResponse entry = Assert.Single(entries);
        Assert.Equal(12000m, entry.Lines.Single(l => l.AccountId == fixture.LossOnDisposalAccountId && l.Direction == "Debit").Amount);
        Assert.DoesNotContain(entry.Lines, l => l.AccountId == fixture.DisposalProceedsAccountId);
    }

    [Fact]
    public async Task A_disposed_asset_is_excluded_from_a_depreciation_run_and_frozen_against_edits()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);
        AssetView asset = await CreateAssetAsync(http, clientId,
            new SaveAssetRequest("Van", 12000m, new DateOnly(2026, 1, 1), 24, 0m, DepreciationMethod.StraightLine, null));
        (await http.PostAsJsonAsync($"/clients/{clientId}/assets/{asset.Asset.Id}/dispose",
            new DisposeAssetRequest(new DateOnly(2026, 3, 31), 5000m, null))).EnsureSuccessStatusCode();

        // A run for a period with only this (now disposed) asset → 422 nothing to depreciate.
        HttpResponseMessage run = await http.PostAsJsonAsync($"/clients/{clientId}/depreciation-runs",
            new RunDepreciationRequest(2026, 4, null, null));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, run.StatusCode);

        // Edit / deactivate a disposed asset → 409.
        HttpResponseMessage edit = await http.PutAsJsonAsync($"/clients/{clientId}/assets/{asset.Asset.Id}",
            new SaveAssetRequest("Van 2", 12000m, new DateOnly(2026, 1, 1), 24, 0m, DepreciationMethod.StraightLine, null));
        Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);
        HttpResponseMessage deact = await http.PostAsync($"/clients/{clientId}/assets/{asset.Asset.Id}/deactivate", null);
        Assert.Equal(HttpStatusCode.Conflict, deact.StatusCode);
    }

    [Fact]
    public async Task Re_dispose_is_rejected_and_void_reinstates_the_asset()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);
        AssetView asset = await CreateAssetAsync(http, clientId,
            new SaveAssetRequest("Van", 12000m, new DateOnly(2026, 1, 1), 24, 0m, DepreciationMethod.StraightLine, null));
        DisposalView disposal = (await (await http.PostAsJsonAsync($"/clients/{clientId}/assets/{asset.Asset.Id}/dispose",
            new DisposeAssetRequest(new DateOnly(2026, 6, 30), 10000m, null))).EnsureSuccessStatusCode()
            .Content.ReadFromJsonAsync<DisposalView>())!;

        // Re-dispose → 409.
        HttpResponseMessage second = await http.PostAsJsonAsync($"/clients/{clientId}/assets/{asset.Asset.Id}/dispose",
            new DisposeAssetRequest(new DateOnly(2026, 7, 31), 1000m, null));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        // Void → asset back to Active with accumulated restored, entry reversed.
        HttpResponseMessage voided = await http.PostAsJsonAsync(
            $"/clients/{clientId}/disposals/{disposal.Disposal.Id}/void", new VoidReasonRequest("unwind"));
        voided.EnsureSuccessStatusCode();
        AssetView after = (await http.GetFromJsonAsync<AssetView>($"/clients/{clientId}/assets/{asset.Asset.Id}"))!;
        Assert.Equal(AssetStatus.Active, after.Asset.Status);
        Assert.Equal(0m, after.Asset.AccumulatedDepreciation);

        EntryResponse[] entries = (await http.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={disposal.Disposal.Id}"))!;
        Assert.Contains(entries, e => e.Status == "Voided" || e.ReversalOf != null);

        // Now disposable again.
        HttpResponseMessage redispose = await http.PostAsJsonAsync($"/clients/{clientId}/assets/{asset.Asset.Id}/dispose",
            new DisposeAssetRequest(new DateOnly(2026, 8, 31), 2000m, null));
        Assert.Equal(HttpStatusCode.Created, redispose.StatusCode);
    }

    [Fact]
    public async Task A_member_without_write_cannot_dispose_and_an_unentitled_client_is_forbidden()
    {
        (Guid clientId, HttpClient controller) = await fixture.SeedClientAsync();
        await SetUpChartAsync(controller, clientId);
        AssetView asset = await CreateAssetAsync(controller, clientId,
            new SaveAssetRequest("Van", 12000m, new DateOnly(2026, 1, 1), 24, 0m, DepreciationMethod.StraightLine, null));

        // Read-only Auditor on the SAME client → 403 (reuse the FA-2 capability seeding pattern).
        Guid auditorUserId = Guid.NewGuid();
        await fixture.Control().AddMembershipAsync(auditorUserId, clientId, Accounting101.Ledger.Api.Control.LedgerRole.Auditor);
        HttpClient auditor = fixture.ClientFor(auditorUserId, "Acme Auditor", Accounting101.Ledger.Api.Control.LedgerRole.Auditor);
        HttpResponseMessage denied = await auditor.PostAsJsonAsync($"/clients/{clientId}/assets/{asset.Asset.Id}/dispose",
            new DisposeAssetRequest(new DateOnly(2026, 6, 30), 1000m, null));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        // Unentitled client → 403.
        (Guid noModuleClient, HttpClient noModule) = await fixture.SeedClientAsync(enabledModules: []);
        HttpResponseMessage ent = await noModule.PostAsJsonAsync($"/clients/{noModuleClient}/assets/{Guid.NewGuid()}/dispose",
            new DisposeAssetRequest(new DateOnly(2026, 6, 30), 1000m, null));
        Assert.Equal(HttpStatusCode.Forbidden, ent.StatusCode);
    }
}
