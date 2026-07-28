using Accounting101.Ledger.Contracts;
using Accounting101.TestSupport;

namespace Accounting101.Inventory.Tests;

/// <summary>
/// Blocks in the PROBE until every racer has arrived, forcing both callers to observe "not found"
/// before either reaches the atomic create — the interleaving InventoryMovementService's pre-check is
/// vulnerable to. Everything else delegates, so the create is really arbitrated by the inner fake's
/// own <c>lock</c>-guarded, insert-only <c>FirstOrDefault</c> existence check in
/// <see cref="InMemoryStockMovementStore.RecordAsync"/> (it has no <c>TryAdd</c> — it is a
/// <c>List</c>, not a dictionary).
///
/// DO NOT WIRE THIS STORE TO A VOID without changing it first, and nothing does today.
/// <c>InventoryMovementService.VoidAsync</c> calls <c>GetAsync</c> THREE times (the item-key pre-read, the
/// re-read under the lock, and the read-back it returns) and this decorator arrives at the rendezvous on
/// EVERY <c>GetAsync</c>, while <see cref="RaceRendezvous"/> is a one-shot COUNTING barrier that releases
/// on the Nth arrival from anyone — so a single voider would satisfy <c>participants: 2</c> by itself, no
/// timeout, no red, no signal that the forced interleaving never happened. Same hazard
/// <see cref="FoldOverlapGate"/>'s remarks describe for the record path, and the same remedy: such a test
/// needs arrival identity (count distinct callers) rather than an arrival count.
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
