using System.Collections.Concurrent;

namespace Accounting101.Inventory;

/// <summary>Serialises writes that read and then move one item's stock position.</summary>
public interface IItemWriteLock
{
    /// <summary>Waits until no other write to this client's item is in flight, then returns the holder.
    /// Dispose it to release; disposing more than once is safe and releases only once.</summary>
    Task<IDisposable> AcquireAsync(Guid clientId, Guid itemId, CancellationToken ct = default);
}

/// <summary>
/// One <see cref="SemaphoreSlim"/> per (client, item), created on first use.
///
/// SINGLE INSTANCE ONLY — ratified 2026-07-27, the sole consumer being the JordanSoft LLC books. This is
/// a variable in one process's memory. Run two API instances and each holds its own, neither sees the
/// other's, and two simultaneous writes landing on different instances would BOTH proceed. That is worse
/// than no lock, because it looks like protection. The replacement, if that day comes, is a
/// compare-and-swap over <c>IDocumentStore.CreateWithIdAsync</c>: claim a token whose id encodes the fold
/// version the caller read, so exactly one writer per version wins and losers re-read and retry — that
/// insert-only create and <c>NextNumberAsync</c> are the only atomic primitives the engine exposes to
/// modules, and modules have no database access to take a real lock.
///
/// THE HOLD SPANS A LOOPBACK HTTP CALL, and <see cref="AcquireAsync"/> has no timeout. Both write paths
/// keep this lock across their <c>ILedgerClient</c> calls — deliberately, since the fold does not move
/// until the entry exists — so a stalled engine queues later writes TO THAT SAME (client, item) for as
/// long as the ledger call takes. That is BOUNDED, not a deadlock: the bound is the module ledger
/// HttpClient's timeout, left at the .NET default of 100s (<c>ModuleLedgerClientServiceExtensions
/// .AddModuleLedgerClient</c> sets only a BaseAddress), waiters are awaited rather than blocked, the
/// <c>using</c> releases on every throw path including that timeout, nothing inside either section
/// re-enters a write path, and an aborted request drops its waiter with no count leak. Deliberately NOT
/// given an acquire timeout: a caller that gave up waiting would proceed to fold unprotected, which is
/// the defect this class exists to prevent. Writes to other items are unaffected — the key is per item.
///
/// The dictionary is never evicted: one semaphore per item ever written, for the process's lifetime.
/// Eviction would need reference counting to avoid disposing a semaphore another caller is waiting on,
/// which is not worth the risk at this scale.
/// </summary>
public sealed class ItemWriteLock : IItemWriteLock
{
    private readonly ConcurrentDictionary<(Guid ClientId, Guid ItemId), SemaphoreSlim> _gates = new();

    public async Task<IDisposable> AcquireAsync(Guid clientId, Guid itemId, CancellationToken ct = default)
    {
        SemaphoreSlim gate = _gates.GetOrAdd((clientId, itemId), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        return new Holder(gate);
    }

    /// <summary>Releases exactly once however many times it is disposed — an over-release would raise the
    /// semaphore's count above one and silently end mutual exclusion.</summary>
    private sealed class Holder(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release();
        }
    }
}
