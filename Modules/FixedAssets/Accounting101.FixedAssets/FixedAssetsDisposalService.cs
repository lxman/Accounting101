using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets;

/// <summary>Orchestrates a disposal: validate, resolve accounts, catch depreciation up to the disposal
/// month, compute gain/loss vs net book value, persist the evidentiary disposal, stamp the asset
/// Disposed, and post one PendingApproval GL entry. Void reverses the entry (tolerating a missing one),
/// reinstates the asset to its pre-disposal accumulated depreciation, and voids the doc. The module never
/// self-approves.</summary>
public sealed class FixedAssetsDisposalService(
    IAssetStore assets,
    IDisposalStore disposals,
    DepreciationMethodSelector methods,
    IFixedAssetsAccountsProvider accounts,
    ILedgerClient ledger)
{
    public async Task<Disposal> DisposeAsync(Guid clientId, Guid assetId, DisposeRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Proceeds < 0m)
            throw new ArgumentException("Proceeds cannot be negative.", nameof(request));

        // 1. Load + guard.
        Asset asset = await assets.GetAsync(clientId, assetId, ct)
            ?? throw new InvalidOperationException($"Asset {assetId} not found or not disposable.");
        if (asset.Status != AssetStatus.Active)
            throw new InvalidOperationException($"Asset {assetId} is {asset.Status}; only an active asset can be disposed.");
        if (request.DisposalDate < asset.InServiceDate)
            throw new ArgumentException("Disposal date is before the asset's in-service date.", nameof(request));
        if (await disposals.GetActiveByAssetAsync(clientId, assetId, ct) is not null)
            throw new InvalidOperationException($"Asset {assetId} already has an active disposal.");

        // 2. Resolve accounts BEFORE any persistence.
        FixedAssetsPostingAccounts postingAccounts = await accounts.GetAccountsAsync(clientId, ct);

        // 3. Compute catch-up + gain/loss.
        IDepreciationMethod method = methods.For(asset.Method);
        int targetMonths = DepreciationSchedule.TargetMonths(asset, request.DisposalDate);
        decimal targetAccumulated = DepreciationSchedule.AccumulatedAfter(method, asset, targetMonths);
        decimal currentAccumulated = asset.AccumulatedDepreciation;
        decimal catchUp = Math.Max(0m, targetAccumulated - currentAccumulated);
        decimal finalAccumulated = currentAccumulated + catchUp;
        decimal nbv = asset.AcquisitionCost - finalAccumulated;
        decimal gainLoss = request.Proceeds - nbv;

        // 4. Persist the evidentiary disposal.
        Disposal disposal = await disposals.RecordAsync(clientId, new DisposalBody(
            assetId, request.DisposalDate, request.Proceeds, catchUp, currentAccumulated, finalAccumulated, nbv, gainLoss, request.Memo), ct);

        // 5. Stamp the asset Disposed with its final accumulated depreciation.
        DisposeStamp stamp = await assets.MarkDisposedAsync(clientId, assetId, finalAccumulated, ct);
        if (stamp.Outcome != DisposeOutcome.Disposed)
            throw new InvalidOperationException($"Asset {assetId} could not be disposed ({stamp.Outcome}).");

        // 6. Compose + post one PendingApproval entry.
        PostEntryRequest entry = FixedAssetsDisposalPosting.ComposeDisposal(
            disposal.Id, request.DisposalDate, asset.AcquisitionCost, currentAccumulated, catchUp, request.Proceeds, gainLoss, request.Memo, postingAccounts);
        await ledger.PostAsync(clientId, entry, ct);

        return disposal;
    }

    public async Task<Disposal> VoidDisposalAsync(Guid clientId, Guid disposalId, string? reason, CancellationToken ct = default)
    {
        Disposal disposal = await disposals.GetAsync(clientId, disposalId, ct)
            ?? throw new InvalidOperationException($"Disposal {disposalId} not found.");
        if (disposal.Status != DisposalStatus.Posted)
            throw new InvalidOperationException($"Only a posted disposal can be voided; {disposalId} is {disposal.Status}.");

        // Reverse the posted entry (or withdraw if still pending); tolerate a missing entry.
        IReadOnlyList<EntryResponse> spawned = await ledger.GetEntriesBySourceRefAsync(clientId, disposalId, ct);
        EntryResponse? entry = spawned.FirstOrDefault(e => e is { Status: "Active", ReversalOf: null });
        if (entry is not null)
        {
            if (entry.Posting == "Posted")
                await ledger.ReverseAsync(clientId, entry.Id, new ReverseRequest(disposal.DisposalDate, reason ?? $"Voided disposal {disposalId}"), ct);
            else
                await ledger.VoidAsync(clientId, entry.Id, new VoidRequest(reason ?? $"Voided disposal {disposalId}"), ct);
        }

        // Reinstate the asset to its pre-disposal accumulated depreciation, then void the doc.
        await assets.ReinstateAsync(clientId, disposal.AssetId, disposal.AccumulatedBeforeDisposal, ct);
        await disposals.VoidAsync(clientId, disposalId, ct);
        return (await disposals.GetAsync(clientId, disposalId, ct))!;
    }

    public Task<Disposal?> GetDisposalAsync(Guid clientId, Guid disposalId, CancellationToken ct = default) =>
        disposals.GetAsync(clientId, disposalId, ct);
}

/// <summary>Input for disposing an asset — the caller supplies the disposal date, proceeds (0 = retirement),
/// and an optional memo; amounts are server-computed.</summary>
public sealed record DisposeRequest(DateOnly DisposalDate, decimal Proceeds, string? Memo);
