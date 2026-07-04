using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets;

/// <summary>Persists disposals as evidentiary documents (created, immediately finalized into a numbered
/// append-only document, voidable). Number + status derive from the engine envelope.</summary>
public sealed class DocumentDisposalStore(IDocumentStore documents) : IDisposalStore
{
    private const string Collection = "disposals";

    public async Task<Disposal> RecordAsync(Guid clientId, DisposalBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        Guid id = await documents.CreateAsync(clientId, Collection, body, Tags(), ct);
        await documents.FinalizeAsync(clientId, Collection, id, ct);
        DocumentResult<DisposalBody>? result = await documents.GetAsync<DisposalBody>(clientId, Collection, id, ct);
        return Map(result!);
    }

    public Task VoidAsync(Guid clientId, Guid disposalId, CancellationToken ct = default) =>
        documents.VoidAsync(clientId, Collection, disposalId, ct);

    public async Task<Disposal?> GetAsync(Guid clientId, Guid disposalId, CancellationToken ct = default)
    {
        DocumentResult<DisposalBody>? result = await documents.GetAsync<DisposalBody>(clientId, Collection, disposalId, ct);
        return result is null ? null : Map(result);
    }

    public async Task<PagedResponse<Disposal>> GetByClientPagedAsync(
        Guid clientId, int skip, int limit, bool descending, bool includeVoided, CancellationToken ct = default)
    {
        IReadOnlyList<DocumentResult<DisposalBody>> page =
            await documents.QueryAsync<DisposalBody>(clientId, Collection, Tags(), skip, limit, descending, includeVoided, ct);
        long total = await documents.CountAsync(clientId, Collection, Tags(), includeVoided, ct);
        return new PagedResponse<Disposal>(page.Select(Map).ToList(), total, skip, limit);
    }

    public async Task<Disposal?> GetActiveByAssetAsync(Guid clientId, Guid assetId, CancellationToken ct = default)
    {
        // Unbounded query (no limit): MongoDocumentStore clamps any supplied limit to 200. includeVoided
        // defaults false → non-voided disposals only.
        IReadOnlyList<DocumentResult<DisposalBody>> all =
            await documents.QueryAsync<DisposalBody>(clientId, Collection, Tags(), cancellationToken: ct);
        DocumentResult<DisposalBody>? hit = all.FirstOrDefault(r => r.Body.AssetId == assetId);
        return hit is null ? null : Map(hit);
    }

    private static Dictionary<string, string> Tags() => new();

    private static Disposal Map(DocumentResult<DisposalBody> r) => new()
    {
        Id = r.Id,
        Number = r.Sequence is { } seq ? $"DP-{seq:D5}" : null,
        AssetId = r.Body.AssetId,
        DisposalDate = r.Body.DisposalDate,
        Proceeds = r.Body.Proceeds,
        CatchUpDepreciation = r.Body.CatchUpDepreciation,
        AccumulatedBeforeDisposal = r.Body.AccumulatedBeforeDisposal,
        AccumulatedAtDisposal = r.Body.AccumulatedAtDisposal,
        NetBookValue = r.Body.NetBookValue,
        GainLoss = r.Body.GainLoss,
        Memo = r.Body.Memo,
        Status = r.State switch
        {
            DocumentLifecycle.Voided or DocumentLifecycle.Superseded => DisposalStatus.Voided,
            _ => DisposalStatus.Posted,
        },
    };
}
