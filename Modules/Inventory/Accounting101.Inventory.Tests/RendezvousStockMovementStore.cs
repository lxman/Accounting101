using Accounting101.Ledger.Contracts;
using Accounting101.TestSupport;

namespace Accounting101.Inventory.Tests;

/// <summary>
/// Blocks in the PROBE until every racer has arrived, forcing both callers to observe "not found"
/// before either reaches the atomic create — the interleaving InventoryMovementService's pre-check is
/// vulnerable to. Everything else delegates, so the create is really arbitrated by the inner fake's
/// TryAdd.
/// </summary>
internal sealed class RendezvousStockMovementStore(
    InMemoryStockMovementStore inner, RaceRendezvous rendezvous) : IStockMovementStore
{
    public async Task<StockMovement?> GetAsync(Guid clientId, Guid movementId, CancellationToken ct = default)
    {
        StockMovement? found = await inner.GetAsync(clientId, movementId, ct);
        await rendezvous.ArriveAndWaitAsync(ct);
        return found;
    }

    public Task<(StockMovement Movement, bool Created)> RecordAsync(
        Guid clientId, Guid id, StockMovementBody body, CancellationToken ct = default) =>
        inner.RecordAsync(clientId, id, body, ct);

    public Task VoidAsync(Guid clientId, Guid movementId, CancellationToken ct = default) =>
        inner.VoidAsync(clientId, movementId, ct);

    public Task<PagedResponse<StockMovement>> GetByItemPagedAsync(
        Guid clientId, Guid itemId, int skip, int limit, bool descending, bool includeVoided, CancellationToken ct = default) =>
        inner.GetByItemPagedAsync(clientId, itemId, skip, limit, descending, includeVoided, ct);

    public Task<StockMovement?> GetLatestForItemAsync(Guid clientId, Guid itemId, CancellationToken ct = default) =>
        inner.GetLatestForItemAsync(clientId, itemId, ct);

    public Task<IReadOnlyList<StockMovement>> GetAllByItemAsync(Guid clientId, Guid itemId, CancellationToken ct = default) =>
        inner.GetAllByItemAsync(clientId, itemId, ct);

    public Task<IReadOnlyList<StockMovement>> GetAllByItemsAsync(Guid clientId, IReadOnlyList<Guid> itemIds, CancellationToken ct = default) =>
        inner.GetAllByItemsAsync(clientId, itemIds, ct);
}
