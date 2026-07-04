using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets;

/// <summary>Orchestrates depreciation: compute one period across all eligible assets, persist the
/// evidentiary run, advance each asset's accumulated depreciation, and post one PendingApproval GL
/// entry. Void is LIFO — only the latest non-voided run may be voided; it reverses the entry (or
/// withdraws it if still pending) and rolls each asset's accumulated depreciation back. The module
/// never self-approves.</summary>
public sealed class FixedAssetsRunService(
    IAssetStore assets,
    IDepreciationRunStore runs,
    DepreciationMethodSelector methods,
    IFixedAssetsAccountsProvider accounts,
    ILedgerClient ledger)
{
    public async Task<DepreciationRun> RunDepreciationAsync(
        Guid clientId, DepreciationRunRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        DepreciationPeriod period = new(request.Year, request.Month);

        // 1. Period guard — one non-voided run per period.
        if (await runs.GetByPeriodAsync(clientId, period, ct) is not null)
            throw new InvalidOperationException($"A depreciation run already exists for {period.Year}-{period.Month:D2}.");

        // 2. Enumerate eligible active assets (in service on/before the period, not fully depreciated).
        List<DepreciationRunLine> lines = [];
        foreach (Asset asset in await ActiveAssetsAsync(clientId, ct))
        {
            if (!period.OnOrAfterServiceMonth(asset.InServiceDate)) continue;
            decimal amount = methods.For(asset.Method).DepreciationForPeriod(asset);
            if (amount > 0m) lines.Add(new DepreciationRunLine(asset.Id, amount));
        }

        // 3. Nothing to depreciate → 422 (no doc, no entry).
        if (lines.Count == 0)
            throw new ArgumentException($"No assets to depreciate for {period.Year}-{period.Month:D2}.");

        decimal total = lines.Sum(l => l.Amount);
        DateOnly effectiveDate = request.EffectiveDate ?? period.LastDay();

        // 4. Persist the evidentiary run.
        DepreciationRun run = await runs.RecordAsync(clientId,
            new DepreciationRunBody(period, effectiveDate, request.Memo, lines, total), ct);

        // 5. Advance each asset's accumulated depreciation.
        await assets.ApplyDepreciationAsync(clientId, lines, ct);

        // 6. Compose + post one PendingApproval aggregate entry.
        FixedAssetsPostingAccounts postingAccounts = await accounts.GetAccountsAsync(clientId, ct);
        PostEntryRequest entry = FixedAssetsPosting.ComposeDepreciationRun(run.Id, total, effectiveDate, request.Memo, postingAccounts);
        await ledger.PostAsync(clientId, entry, ct);

        return run;
    }

    public async Task<DepreciationRun> VoidRunAsync(Guid clientId, Guid runId, string? reason, CancellationToken ct = default)
    {
        DepreciationRun run = await runs.GetAsync(clientId, runId, ct)
            ?? throw new InvalidOperationException($"Depreciation run {runId} not found.");
        if (run.Status != DepreciationRunStatus.Posted)
            throw new InvalidOperationException($"Only a posted depreciation run can be voided; {runId} is {run.Status}.");

        // LIFO — only the most recent non-voided run may be voided.
        DepreciationRun? latest = await runs.GetLatestAsync(clientId, ct);
        if (latest is null || latest.Id != run.Id)
            throw new InvalidOperationException("Only the most recent depreciation run can be voided.");

        // Reverse the posted entry (or withdraw it if still pending) — Payroll precedent.
        IReadOnlyList<EntryResponse> spawned = await ledger.GetEntriesBySourceRefAsync(clientId, runId, ct);
        EntryResponse entry = spawned.FirstOrDefault(e => e is { Status: "Active", ReversalOf: null })
            ?? throw new InvalidOperationException($"No entry found for depreciation run {run.Number} to void.");
        if (entry.Posting == "Posted")
            await ledger.ReverseAsync(clientId, entry.Id, new ReverseRequest(run.EffectiveDate, reason ?? $"Voided depreciation run {runId}"), ct);
        else
            await ledger.VoidAsync(clientId, entry.Id, new VoidRequest(reason ?? $"Voided depreciation run {runId}"), ct);

        // Roll each asset's accumulated depreciation back, then void the doc.
        await assets.ReverseDepreciationAsync(clientId, run.Lines, ct);
        await runs.VoidAsync(clientId, runId, ct);
        return (await runs.GetAsync(clientId, runId, ct))!;
    }

    public Task<DepreciationRun?> GetRunAsync(Guid clientId, Guid runId, CancellationToken ct = default) =>
        runs.GetAsync(clientId, runId, ct);

    /// <summary>All active (non-deactivated) assets for the client — the depreciation candidate set.</summary>
    private async Task<IReadOnlyList<Asset>> ActiveAssetsAsync(Guid clientId, CancellationToken ct)
    {
        List<Asset> all = [];
        int skip = 0;
        const int page = 200;
        while (true)
        {
            PagedResponse<Asset> batch = await assets.GetByClientPagedAsync(clientId, skip, page, descending: false, includeInactive: false, ct);
            all.AddRange(batch.Items);
            skip += page;
            if (all.Count >= batch.Total || batch.Items.Count == 0) break;
        }
        return all;
    }
}

/// <summary>Orchestration input for a depreciation run — the caller supplies only the period and optional
/// overrides; amounts are server-computed.</summary>
public sealed record DepreciationRunRequest(int Year, int Month, DateOnly? EffectiveDate, string? Memo);
