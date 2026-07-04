using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets;

/// <summary>Persists depreciation runs as evidentiary documents (created, immediately finalized into a
/// numbered append-only document, voidable). Number + status derive from the engine envelope. The
/// module owns no database connection.</summary>
public sealed class DocumentDepreciationRunStore(IDocumentStore documents) : IDepreciationRunStore
{
    private const string Collection = "depreciation-runs";

    public async Task<DepreciationRun> RecordAsync(Guid clientId, DepreciationRunBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        Guid id = await documents.CreateAsync(clientId, Collection, body, Tags(), ct);
        await documents.FinalizeAsync(clientId, Collection, id, ct);
        DocumentResult<DepreciationRunBody>? result = await documents.GetAsync<DepreciationRunBody>(clientId, Collection, id, ct);
        return Map(result!);
    }

    public Task VoidAsync(Guid clientId, Guid runId, CancellationToken ct = default) =>
        documents.VoidAsync(clientId, Collection, runId, ct);

    public async Task<DepreciationRun?> GetAsync(Guid clientId, Guid runId, CancellationToken ct = default)
    {
        DocumentResult<DepreciationRunBody>? result = await documents.GetAsync<DepreciationRunBody>(clientId, Collection, runId, ct);
        return result is null ? null : Map(result);
    }

    public async Task<PagedResponse<DepreciationRun>> GetByClientPagedAsync(
        Guid clientId, int skip, int limit, bool descending, bool includeVoided, CancellationToken ct = default)
    {
        IReadOnlyList<DocumentResult<DepreciationRunBody>> page =
            await documents.QueryAsync<DepreciationRunBody>(clientId, Collection, Tags(), skip, limit, descending, includeVoided, ct);
        long total = await documents.CountAsync(clientId, Collection, Tags(), includeVoided, ct);
        return new PagedResponse<DepreciationRun>(page.Select(Map).ToList(), total, skip, limit);
    }

    public async Task<DepreciationRun?> GetByPeriodAsync(Guid clientId, DepreciationPeriod period, CancellationToken ct = default)
    {
        // Unbounded query (no limit): MongoDocumentStore clamps any supplied limit to 200, which would
        // silently truncate the period guard. includeVoided defaults false → non-voided runs only.
        IReadOnlyList<DocumentResult<DepreciationRunBody>> all =
            await documents.QueryAsync<DepreciationRunBody>(clientId, Collection, Tags(), cancellationToken: ct);
        DocumentResult<DepreciationRunBody>? hit = all.FirstOrDefault(r => r.Body.Period == period);
        return hit is null ? null : Map(hit);
    }

    public async Task<DepreciationRun?> GetLatestAsync(Guid clientId, CancellationToken ct = default)
    {
        IReadOnlyList<DocumentResult<DepreciationRunBody>> all =
            await documents.QueryAsync<DepreciationRunBody>(clientId, Collection, Tags(), 0, 1, descending: true, includeVoided: false, ct);
        DocumentResult<DepreciationRunBody>? latest = all.FirstOrDefault();
        return latest is null ? null : Map(latest);
    }

    private static Dictionary<string, string> Tags() => new();

    private static DepreciationRun Map(DocumentResult<DepreciationRunBody> r) => new()
    {
        Id = r.Id,
        Number = r.Sequence is { } seq ? $"DR-{seq:D5}" : null,
        Period = r.Body.Period,
        EffectiveDate = r.Body.EffectiveDate,
        Memo = r.Body.Memo,
        Lines = r.Body.Lines,
        Total = r.Body.Total,
        Status = r.State switch
        {
            DocumentLifecycle.Voided or DocumentLifecycle.Superseded => DepreciationRunStatus.Voided,
            _ => DepreciationRunStatus.Posted,
        },
    };
}
