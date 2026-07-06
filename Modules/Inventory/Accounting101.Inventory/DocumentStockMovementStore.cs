using Accounting101.Ledger.Contracts;

namespace Accounting101.Inventory;

/// <summary>Persists stock movements as evidentiary documents (created, immediately finalized into a
/// numbered append-only document, voidable). Number + status derive from the engine envelope. The
/// module owns no database connection.</summary>
public sealed class DocumentStockMovementStore(IDocumentStore documents) : IStockMovementStore
{
    private const string Collection = "stock-movements";

    public async Task<StockMovement> RecordAsync(Guid clientId, StockMovementBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        Guid id = await documents.CreateAsync(clientId, Collection, body, Tags(), ct);
        await documents.FinalizeAsync(clientId, Collection, id, ct);
        DocumentResult<StockMovementBody>? result = await documents.GetAsync<StockMovementBody>(clientId, Collection, id, ct);
        return Map(result!);
    }

    public Task VoidAsync(Guid clientId, Guid movementId, CancellationToken ct = default) =>
        documents.VoidAsync(clientId, Collection, movementId, ct);

    public async Task<StockMovement?> GetAsync(Guid clientId, Guid movementId, CancellationToken ct = default)
    {
        DocumentResult<StockMovementBody>? result = await documents.GetAsync<StockMovementBody>(clientId, Collection, movementId, ct);
        return result is null ? null : Map(result);
    }

    public async Task<PagedResponse<StockMovement>> GetByItemPagedAsync(
        Guid clientId, Guid itemId, int skip, int limit, bool descending, bool includeVoided, CancellationToken ct = default)
    {
        // Unbounded query (no limit): MongoDocumentStore clamps any supplied limit to 200, which would
        // silently truncate the per-item filter+page below. includeVoided:true so we can filter voided
        // out ourselves per-item after retrieving; the doc store has no per-field query.
        IReadOnlyList<DocumentResult<StockMovementBody>> all =
            await documents.QueryAsync<StockMovementBody>(clientId, Collection, Tags(), includeVoided: true, cancellationToken: ct);

        IEnumerable<DocumentResult<StockMovementBody>> filtered = all.Where(r => r.Body.ItemId == itemId);
        if (!includeVoided)
            filtered = filtered.Where(r => r.State is not (DocumentLifecycle.Voided or DocumentLifecycle.Superseded));

        List<DocumentResult<StockMovementBody>> ordered = (descending
            ? filtered.OrderByDescending(r => r.Sequence)
            : filtered.OrderBy(r => r.Sequence)).ToList();

        long total = ordered.Count;
        List<StockMovement> page = ordered.Skip(skip).Take(limit).Select(Map).ToList();
        return new PagedResponse<StockMovement>(page, total, skip, limit);
    }

    public async Task<StockMovement?> GetLatestForItemAsync(Guid clientId, Guid itemId, CancellationToken ct = default)
    {
        // Unbounded query (no limit): MongoDocumentStore clamps any supplied limit to 200, which would
        // silently truncate the per-item scan below. includeVoided defaults false → non-voided movements only.
        IReadOnlyList<DocumentResult<StockMovementBody>> all =
            await documents.QueryAsync<StockMovementBody>(clientId, Collection, Tags(), cancellationToken: ct);
        DocumentResult<StockMovementBody>? latest = all
            .Where(r => r.Body.ItemId == itemId)
            .OrderByDescending(r => r.Sequence)
            .FirstOrDefault();
        return latest is null ? null : Map(latest);
    }

    private static Dictionary<string, string> Tags() => new();

    private static StockMovement Map(DocumentResult<StockMovementBody> r) => new()
    {
        Id = r.Id,
        Number = r.Sequence is { } seq ? $"MV-{seq:D5}" : null,
        ItemId = r.Body.ItemId,
        Type = r.Body.Type,
        Quantity = r.Body.Quantity,
        UnitCost = r.Body.UnitCost,
        Value = r.Body.Value,
        ResultingOnHandQuantity = r.Body.ResultingOnHandQuantity,
        ResultingTotalValue = r.Body.ResultingTotalValue,
        EffectiveDate = r.Body.EffectiveDate,
        Memo = r.Body.Memo,
        Status = r.State switch
        {
            DocumentLifecycle.Voided or DocumentLifecycle.Superseded => MovementStatus.Void,
            _ => MovementStatus.Posted,
        },
    };
}
