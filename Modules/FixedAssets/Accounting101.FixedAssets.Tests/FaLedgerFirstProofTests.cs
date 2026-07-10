using System.Net.Http.Json;
using Accounting101.FixedAssets.Api;
using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets.Tests;

/// <summary>
/// The FA ledger-first proof suite — codifies the redesign's remaining merge-gate obligations as explicit
/// end-to-end tests. Most obligations (fold/sign + per-asset independence, the depreciation entry's
/// dimensioned shape, disposal clearing the fold, the untagged-accum 422) are already proven exactly, at
/// the E2E level, by <see cref="DepreciationRunE2eTests"/>/<see cref="DisposalE2eTests"/> — those are not
/// duplicated here. This file adds only the two obligations those files leave open:
/// <list type="bullet">
///   <item>the disposal entry's Accumulated Depreciation debit line carries the Asset dimension tag for
///     the REAL server-issued asset id (a Task-2 coverage gap: the existing disposal tests assert the
///     Gain/Loss line but never inspect the accum line's dimension tag);</item>
///   <item>the headline void-auto-rollback payoff: voiding a POSTED (approved) run — not merely a pending
///     one — reverses its entry through the ledger and the asset's reported accumulated depreciation
///     returns to its PRIOR non-zero value (not just to 0), with no manual rollback anywhere in the call
///     path — the fold recomputes on the next read.</item>
/// </list>
/// </summary>
public sealed class FaLedgerFirstProofTests(FixedAssetsHostFixture fixture) : IClassFixture<FixedAssetsHostFixture>
{
    private async Task SetUpChartAsync(HttpClient http, Guid clientId)
    {
        await PutAccountAsync(http, clientId, fixture.DepreciationExpenseAccountId,     "6200", "Depreciation Expense",     "Expense");
        await PutAccountAsync(http, clientId, fixture.AccumulatedDepreciationAccountId, "1590", "Accumulated Depreciation", "Asset",
            requiredDimensions: ["Asset"]);
        await PutAccountAsync(http, clientId, fixture.AssetCostAccountId,        "1500", "Fixed Assets",     "Asset");
        await PutAccountAsync(http, clientId, fixture.DisposalProceedsAccountId, "1000", "Cash",             "Asset");
        await PutAccountAsync(http, clientId, fixture.GainOnDisposalAccountId,   "7100", "Gain on Disposal", "Revenue");
        await PutAccountAsync(http, clientId, fixture.LossOnDisposalAccountId,   "7200", "Loss on Disposal", "Expense");
    }

    private static async Task PutAccountAsync(HttpClient http, Guid clientId, Guid accountId, string number, string name, string type,
        IReadOnlyList<string>? requiredDimensions = null) =>
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{accountId}",
            new AccountRequest { Number = number, Name = name, Type = type, RequiredDimensions = requiredDimensions }))
            .EnsureSuccessStatusCode();

    private static async Task<AssetView> CreateAssetAsync(HttpClient http, Guid clientId, SaveAssetRequest req) =>
        (await (await http.PostAsJsonAsync($"/clients/{clientId}/assets", req)).EnsureSuccessStatusCode()
            .Content.ReadFromJsonAsync<AssetView>())!;

    /// <summary>Closes the Task-2 coverage gap: <see cref="DisposalE2eTests"/> asserts the Gain/Loss line
    /// of a disposal entry but never inspects the Accumulated Depreciation line's dimension tag. This
    /// proves that line carries <c>{Asset = assetId}</c> for the REAL server-issued asset id — the same
    /// dimensioning discipline the depreciation-run entry already proves (Accumulated_depreciation...
    /// rejects an untagged line makes the tag structurally REQUIRED; this proves the recipe actually
    /// supplies it, and supplies the right value, not just any value).
    /// <para>The recipe (<see cref="FixedAssetsDisposalPosting.ComposeDisposal"/>) only emits the accum
    /// debit line when <c>currentAccumulated &gt; 0</c> — i.e. when depreciation was already posted to the
    /// books BEFORE disposal — so a January run is recorded and approved first (500 pre-existing) ahead of
    /// a March disposal. Full-month convention excludes the disposal month, so the target by March is 2
    /// months (Jan+Feb = 1000): a further 500 is caught up and expensed, but only the pre-existing 500
    /// clears through the dimensioned accum line.</para></summary>
    [Fact]
    public async Task Disposal_entrys_accumulated_depreciation_line_carries_the_real_assets_dimension_tag()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);
        AssetView asset = await CreateAssetAsync(http, clientId,
            new SaveAssetRequest("Van", 12000m, new DateOnly(2026, 1, 1), 24, 0m, DepreciationMethod.StraightLine, null)); // 500/mo

        // January run, recorded and approved — 500 pre-existing accumulated on the books before disposal.
        DepreciationRunView jan = (await (await http.PostAsJsonAsync($"/clients/{clientId}/depreciation-runs",
            new RunDepreciationRequest(2026, 1, null, null))).EnsureSuccessStatusCode()
            .Content.ReadFromJsonAsync<DepreciationRunView>())!;
        EntryResponse janEntry = Assert.Single((await http.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={jan.Run.Id}"))!);
        (await http.PostAsync($"/clients/{clientId}/entries/{janEntry.Id}/approve", null)).EnsureSuccessStatusCode();

        // Dispose Mar 2026: target by Mar (disposal month excluded) = Jan+Feb = 1000; catch-up = 500 on top
        // of the 500 pre-existing (folded) accum.
        DisposalView disposal = (await (await http.PostAsJsonAsync($"/clients/{clientId}/assets/{asset.Asset.Id}/dispose",
            new DisposeAssetRequest(new DateOnly(2026, 3, 31), 10000m, "sold"))).EnsureSuccessStatusCode()
            .Content.ReadFromJsonAsync<DisposalView>())!;
        Assert.Equal(1000m, disposal.Disposal.AccumulatedAtDisposal); // 500 pre-existing + 500 catch-up

        EntryResponse entry = Assert.Single((await http.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={disposal.Disposal.Id}"))!);

        EntryLineResponse accumLine = Assert.Single(entry.Lines, l =>
            l.AccountId == fixture.AccumulatedDepreciationAccountId);
        Assert.Equal("Debit", accumLine.Direction);
        Assert.Equal(500m, accumLine.Amount); // the PRE-EXISTING (folded) accum cleared by this line
        Assert.True(accumLine.Dimensions.TryGetValue("Asset", out Guid taggedAssetId));
        Assert.Equal(asset.Asset.Id, taggedAssetId); // the REAL server-issued asset id, not a placeholder

        // Sanity: once approved, the asset's reported accumulated depreciation is fully cleared.
        (await http.PostAsync($"/clients/{clientId}/entries/{entry.Id}/approve", null)).EnsureSuccessStatusCode();
        AssetView after = (await http.GetFromJsonAsync<AssetView>($"/clients/{clientId}/assets/{asset.Asset.Id}"))!;
        Assert.Equal(0m, after.Asset.AccumulatedDepreciation);
    }

    /// <summary>The headline void-auto-rollback payoff. Two months are run and approved (Jan then Feb),
    /// so the asset's fold-derived accumulated depreciation sits at a non-zero PRIOR value (500, from Jan)
    /// before the run under test. Voiding the LATEST posted run (Feb) reverses its already-POSTED entry —
    /// not a withdrawal of a still-pending one, which the existing
    /// <see cref="DepreciationRunE2eTests.Void_latest_run_reverses_entry_and_rolls_back_accumulated"/>
    /// leaves ambiguous. Nothing in this test, or in <see cref="FixedAssetsRunService.VoidRunAsync"/>,
    /// touches the asset document or writes any accumulated-depreciation value — the assertion reads the
    /// SAME fold-backed <c>GET /assets/{id}</c> endpoint before and after, and it returns to Jan's 500
    /// (not to 0) purely because the ledger fold recomputes once Feb's entry is reversed.</summary>
    [Fact]
    public async Task Void_of_a_posted_run_reverses_the_entry_and_restores_the_assets_prior_accumulated_with_no_manual_rollback()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);
        AssetView asset = await CreateAssetAsync(http, clientId,
            new SaveAssetRequest("Van", 12000m, new DateOnly(2026, 1, 1), 24, 0m, DepreciationMethod.StraightLine, null)); // 500/mo

        // January — recorded and approved. Prior value the void must restore.
        DepreciationRunView jan = (await (await http.PostAsJsonAsync($"/clients/{clientId}/depreciation-runs",
            new RunDepreciationRequest(2026, 1, null, null))).EnsureSuccessStatusCode()
            .Content.ReadFromJsonAsync<DepreciationRunView>())!;
        EntryResponse janEntry = Assert.Single((await http.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={jan.Run.Id}"))!);
        (await http.PostAsync($"/clients/{clientId}/entries/{janEntry.Id}/approve", null)).EnsureSuccessStatusCode();

        AssetView afterJan = (await http.GetFromJsonAsync<AssetView>($"/clients/{clientId}/assets/{asset.Asset.Id}"))!;
        Assert.Equal(500m, afterJan.Asset.AccumulatedDepreciation);

        // February — recorded and approved. The run under test.
        DepreciationRunView feb = (await (await http.PostAsJsonAsync($"/clients/{clientId}/depreciation-runs",
            new RunDepreciationRequest(2026, 2, null, null))).EnsureSuccessStatusCode()
            .Content.ReadFromJsonAsync<DepreciationRunView>())!;
        EntryResponse febEntry = Assert.Single((await http.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={feb.Run.Id}"))!);
        (await http.PostAsync($"/clients/{clientId}/entries/{febEntry.Id}/approve", null)).EnsureSuccessStatusCode();

        AssetView afterFeb = (await http.GetFromJsonAsync<AssetView>($"/clients/{clientId}/assets/{asset.Asset.Id}"))!;
        Assert.Equal(1000m, afterFeb.Asset.AccumulatedDepreciation);

        // Void February — the latest posted run. Its already-approved entry is REVERSED (not withdrawn):
        // a new Reversing entry is booked, linked via ReversalOf, and — like any entry — lands PENDING.
        // The module never self-approves it (VoidRunAsync only books the reversal).
        HttpResponseMessage voided = await http.PostAsJsonAsync(
            $"/clients/{clientId}/depreciation-runs/{feb.Run.Id}/void", new VoidReasonRequest("entered in error"));
        voided.EnsureSuccessStatusCode();

        EntryResponse[] febEntries = (await http.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={feb.Run.Id}"))!;
        EntryResponse reversal = Assert.Single(febEntries, e => e.ReversalOf is not null); // proves it was POSTED, not withdrawn
        Assert.Equal("PendingApproval", reversal.Posting);

        // Before the reversal is approved, the Posted-only fold hasn't moved yet — February's original
        // debit/credit still stands unreversed on the books.
        AssetView beforeReversalApproval = (await http.GetFromJsonAsync<AssetView>($"/clients/{clientId}/assets/{asset.Asset.Id}"))!;
        Assert.Equal(1000m, beforeReversalApproval.Asset.AccumulatedDepreciation);

        (await http.PostAsync($"/clients/{clientId}/entries/{reversal.Id}/approve", null)).EnsureSuccessStatusCode();

        // The asset's reported accumulated depreciation returns to January's PRIOR value — auto-rollback,
        // no manual step anywhere in FixedAssetsRunService.VoidRunAsync or the asset store: the fold simply
        // recomputes, on the next read, from February's original entry netted against its now-posted reversal.
        AssetView afterVoid = (await http.GetFromJsonAsync<AssetView>($"/clients/{clientId}/assets/{asset.Asset.Id}"))!;
        Assert.Equal(500m, afterVoid.Asset.AccumulatedDepreciation);
    }
}
