using Accounting101.Ledger.Contracts;

namespace Accounting101.Inventory.Tests;

/// <summary>
/// A ONE-SIDED gate that parks a caller inside <c>InventoryMovementService.VoidAsync</c>'s critical
/// section until the TEST thread opens it — the shape a hold test needs, and deliberately NOT a
/// <see cref="TestSupport.RaceRendezvous"/>.
///
/// WHY NOT A RENDEZVOUS. A rendezvous releases when the Nth participant arrives. Inside a section the
/// item lock protects, only ONE party can ever be in there; the other is parked on the semaphore and
/// can never arrive, so a two-party rendezvous placed here would strand and die on its timeout. That is
/// exactly the failure <see cref="RendezvousItemWriteLock"/>'s remarks describe. Here the second
/// "participant" is the test itself, which is not competing for the lock, so the gate always opens.
///
/// WHERE IT BLOCKS, and why that placement is load-bearing. Only
/// <see cref="GetLatestForItemAsync"/> is intercepted; the other six members delegate untouched (the
/// <see cref="FoldCompleteRendezvousLedger"/> shape). That method is called from EXACTLY ONE place in
/// the whole module — <c>InventoryMovementService.VoidAsync</c>'s LIFO guard, four statements after the
/// <c>using</c> acquire and before the method returns — so a caller parked here is provably mid-body
/// under the item lock. Neither <c>RecordAsync</c> nor <see cref="ItemValuationService"/> calls it, so a
/// service built over the UNDECORATED store can never arrive here and can only ever be stopped by the
/// item lock itself.
///
/// It blocks BEFORE delegating, so the LIFO read the void's guard evaluates is taken AFTER the gate
/// opens. That turns "did the void hold the lock?" into an observable domain outcome: if a concurrent
/// record slipped in while the void was parked, the item's latest movement is no longer the one being
/// voided and the guard throws.
///
/// The timeout is load-bearing for the same reason it is on RaceRendezvous: a mis-wired test that never
/// opens the gate must fail in seconds with a clear message, not hang the suite.
/// </summary>
internal sealed class VoidSectionGateStore(IStockMovementStore inner, TimeSpan timeout) : IStockMovementStore
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once a caller has reached the gate — i.e. is inside the void's critical section.</summary>
    public Task Entered => _entered.Task;

    /// <summary>Lets the parked caller run on. Safe to call more than once.</summary>
    public void Open() => _opened.TrySetResult();

    public async Task<StockMovement?> GetLatestForItemAsync(Guid clientId, Guid itemId, CancellationToken ct = default)
    {
        _entered.TrySetResult();
        await _opened.Task.WaitAsync(timeout, ct);
        return await inner.GetLatestForItemAsync(clientId, itemId, ct);
    }

    public Task<(StockMovement Movement, bool Created)> RecordAsync(
        Guid clientId, Guid id, StockMovementBody body, CancellationToken ct = default) =>
        inner.RecordAsync(clientId, id, body, ct);

    public Task VoidAsync(Guid clientId, Guid movementId, CancellationToken ct = default) =>
        inner.VoidAsync(clientId, movementId, ct);

    public Task<StockMovement?> GetAsync(Guid clientId, Guid movementId, CancellationToken ct = default) =>
        inner.GetAsync(clientId, movementId, ct);

    public Task<PagedResponse<StockMovement>> GetByItemPagedAsync(
        Guid clientId, Guid itemId, int skip, int limit, bool descending, bool includeVoided, CancellationToken ct = default) =>
        inner.GetByItemPagedAsync(clientId, itemId, skip, limit, descending, includeVoided, ct);

    public Task<IReadOnlyList<StockMovement>> GetAllByItemAsync(Guid clientId, Guid itemId, CancellationToken ct = default) =>
        inner.GetAllByItemAsync(clientId, itemId, ct);

    public Task<IReadOnlyList<StockMovement>> GetAllByItemsAsync(Guid clientId, IReadOnlyList<Guid> itemIds, CancellationToken ct = default) =>
        inner.GetAllByItemsAsync(clientId, itemIds, ct);
}
