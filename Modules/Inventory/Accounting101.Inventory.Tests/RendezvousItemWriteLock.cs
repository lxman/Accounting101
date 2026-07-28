using Accounting101.TestSupport;

namespace Accounting101.Inventory.Tests;

/// <summary>
/// Forces racers to be genuinely in flight at the same instant, by arriving at the rendezvous BEFORE
/// delegating to the real lock.
///
/// The placement is the whole point and differs deliberately from <see cref="RendezvousStockMovementStore"/>,
/// which blocks inside the store's probe. That probe is inside the section the lock protects, so once
/// callers serialise only one racer can be in there at a time, the second never arrives, and
/// <see cref="RaceRendezvous"/> throws TimeoutException. Arriving before the acquire keeps the rendezvous
/// outside the protected section, so both racers always reach it and the lock alone decides the outcome.
/// </summary>
internal sealed class RendezvousItemWriteLock(IItemWriteLock inner, RaceRendezvous rendezvous) : IItemWriteLock
{
    public async Task<IDisposable> AcquireAsync(Guid clientId, Guid itemId, CancellationToken ct = default)
    {
        await rendezvous.ArriveAndWaitAsync(ct);
        return await inner.AcquireAsync(clientId, itemId, ct);
    }
}
