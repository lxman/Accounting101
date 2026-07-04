using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets;

/// <summary>Persists assets through the engine's document store as reference data (mutable, audited,
/// deactivatable). Server-owned fields — Status and AccumulatedDepreciation — are stamped by the store:
/// Active/0 on create, preserved on update. The module speaks only IDocumentStore.</summary>
public sealed class DocumentAssetStore(IDocumentStore documents) : IAssetStore
{
    private const string Collection = "assets";
    private static readonly Dictionary<string, string> NoTags = new();

    public async Task<Asset> CreateAsync(Guid clientId, AssetBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        Guid id = Guid.NewGuid();
        AssetDocument doc = ToDocument(body, AssetStatus.Active, 0m);
        await documents.PutAsync(clientId, Collection, id, doc, NoTags, ct);
        return Map(id, doc);
    }

    public async Task<UpdateResult> UpdateAsync(Guid clientId, Guid assetId, AssetBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        DocumentResult<AssetDocument>? existing = await documents.GetAsync<AssetDocument>(clientId, Collection, assetId, ct);
        if (existing is null) return UpdateResult.NotFound;
        if (existing.Body.Status == AssetStatus.Disposed) return UpdateResult.Disposed; // frozen until void
        if (existing.State == DocumentLifecycle.Inactive) return UpdateResult.Inactive; // sticky: reactivate first
        // Only the editable params change; Status + AccumulatedDepreciation are preserved (FA-2/FA-3 own them).
        AssetDocument doc = ToDocument(body, existing.Body.Status, existing.Body.AccumulatedDepreciation);
        await documents.PutAsync(clientId, Collection, assetId, doc, NoTags, ct);
        return UpdateResult.Updated(Map(assetId, doc));
    }

    public async Task<DeactivateResult> DeactivateAsync(Guid clientId, Guid assetId, CancellationToken ct = default)
    {
        DocumentResult<AssetDocument>? existing = await documents.GetAsync<AssetDocument>(clientId, Collection, assetId, ct);
        if (existing is null) return DeactivateResult.NotFound;
        if (existing.Body.Status == AssetStatus.Disposed) return DeactivateResult.Disposed;
        if (existing.State == DocumentLifecycle.Inactive) return DeactivateResult.AlreadyInactive;
        await documents.DeactivateAsync(clientId, Collection, assetId, ct);
        return DeactivateResult.Deactivated;
    }

    public async Task<ReactivateResult> ReactivateAsync(Guid clientId, Guid assetId, CancellationToken ct = default)
    {
        DocumentResult<AssetDocument>? existing = await documents.GetAsync<AssetDocument>(clientId, Collection, assetId, ct);
        if (existing is null) return ReactivateResult.NotFound;
        if (existing.Body.Status == AssetStatus.Disposed) return ReactivateResult.Disposed;
        if (existing.State != DocumentLifecycle.Inactive) return ReactivateResult.AlreadyActive;
        // The engine has no explicit reactivate primitive; a Put on a reference doc rebuilds it Active
        // (ScopedDocumentStore.PutReferenceAsync always sets DocumentState.Active). Re-put the SAME body
        // (preserving server-owned Status + AccumulatedDepreciation) so only the lifecycle flips.
        await documents.PutAsync(clientId, Collection, assetId, existing.Body, NoTags, ct);
        return ReactivateResult.Reactivated;
    }

    public async Task ApplyDepreciationAsync(Guid clientId, IReadOnlyList<DepreciationRunLine> lines, CancellationToken ct = default) =>
        await AdjustAccumulatedAsync(clientId, lines, sign: +1m, ct);

    public async Task ReverseDepreciationAsync(Guid clientId, IReadOnlyList<DepreciationRunLine> lines, CancellationToken ct = default) =>
        await AdjustAccumulatedAsync(clientId, lines, sign: -1m, ct);

    private async Task AdjustAccumulatedAsync(Guid clientId, IReadOnlyList<DepreciationRunLine> lines, decimal sign, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lines);
        foreach (DepreciationRunLine line in lines)
        {
            DocumentResult<AssetDocument>? existing = await documents.GetAsync<AssetDocument>(clientId, Collection, line.AssetId, ct);
            if (existing is null) continue; // asset gone; skip (run void tolerates missing assets)
            decimal updated = existing.Body.AccumulatedDepreciation + sign * line.Amount;
            AssetDocument doc = existing.Body with { AccumulatedDepreciation = updated };
            await documents.PutAsync(clientId, Collection, line.AssetId, doc, NoTags, ct);
        }
    }

    public async Task<DisposeStamp> MarkDisposedAsync(Guid clientId, Guid assetId, decimal finalAccumulated, CancellationToken ct = default)
    {
        DocumentResult<AssetDocument>? existing = await documents.GetAsync<AssetDocument>(clientId, Collection, assetId, ct);
        if (existing is null) return new DisposeStamp(DisposeOutcome.NotFound, null, 0m);
        if (existing.Body.Status != AssetStatus.Active) return new DisposeStamp(DisposeOutcome.NotActive, null, existing.Body.AccumulatedDepreciation);
        decimal prior = existing.Body.AccumulatedDepreciation;
        AssetDocument doc = existing.Body with { Status = AssetStatus.Disposed, AccumulatedDepreciation = finalAccumulated };
        await documents.PutAsync(clientId, Collection, assetId, doc, NoTags, ct);
        return new DisposeStamp(DisposeOutcome.Disposed, Map(assetId, doc), prior);
    }

    public async Task ReinstateAsync(Guid clientId, Guid assetId, decimal restoreAccumulated, CancellationToken ct = default)
    {
        DocumentResult<AssetDocument>? existing = await documents.GetAsync<AssetDocument>(clientId, Collection, assetId, ct);
        if (existing is null) return; // asset gone; nothing to reinstate (disposal void tolerates it)
        AssetDocument doc = existing.Body with { Status = AssetStatus.Active, AccumulatedDepreciation = restoreAccumulated };
        await documents.PutAsync(clientId, Collection, assetId, doc, NoTags, ct);
    }

    public async Task<Asset?> GetAsync(Guid clientId, Guid assetId, CancellationToken ct = default)
    {
        DocumentResult<AssetDocument>? result = await documents.GetAsync<AssetDocument>(clientId, Collection, assetId, ct);
        return result is null ? null : Map(result.Id, result.Body);
    }

    public async Task<PagedResponse<Asset>> GetByClientPagedAsync(
        Guid clientId, int skip, int limit, bool descending, bool includeInactive, CancellationToken ct = default)
    {
        IReadOnlyList<DocumentResult<AssetDocument>> page =
            await documents.QueryAsync<AssetDocument>(clientId, Collection, NoTags, skip, limit, descending, includeInactive, ct);
        long total = await documents.CountAsync(clientId, Collection, NoTags, includeInactive, ct);
        return new PagedResponse<Asset>(page.Select(r => Map(r.Id, r.Body)).ToList(), total, skip, limit);
    }

    private static AssetDocument ToDocument(AssetBody body, AssetStatus status, decimal accumulated) =>
        new(body.Description, body.AcquisitionCost, body.InServiceDate, body.UsefulLifeMonths,
            body.SalvageValue, body.Method, body.DecliningBalanceFactor, status, accumulated);

    private static Asset Map(Guid id, AssetDocument d) => new()
    {
        Id = id, Description = d.Description, AcquisitionCost = d.AcquisitionCost, InServiceDate = d.InServiceDate,
        UsefulLifeMonths = d.UsefulLifeMonths, SalvageValue = d.SalvageValue, Method = d.Method,
        DecliningBalanceFactor = d.DecliningBalanceFactor, Status = d.Status, AccumulatedDepreciation = d.AccumulatedDepreciation,
    };
}
