using Accounting101.TestSupport;

namespace Accounting101.Inventory.Tests;

/// <summary>
/// Makes two racers' folds provably OVERLAP whenever the item write lock is not doing its job, and does
/// nothing at all when it is. A racer that reaches the end of its fold while HOLDING the lock is let
/// straight through; a racer that reaches it WITHOUT holding the lock waits there until every other racer
/// has finished folding too.
///
/// WHY THAT CONDITION IS THE WHOLE MECHANISM. In the shipped
/// <see cref="InventoryMovementService.RecordAsync"/> the fold sits inside
/// <c>using IDisposable itemGate = await itemLock.AcquireAsync(...)</c>, so a racer ALWAYS holds the lock
/// when its fold completes — the wait below is unreachable and this gate is inert. Under the
/// acquire-but-don't-hold mutation (<c>(await itemLock.AcquireAsync(...)).Dispose();</c>) a racer has
/// released before it folds, so EVERY racer arrives, the barrier releases them together, and every
/// racer's fold is therefore taken from a state that no racer has written to yet. Two issues of 6 against
/// an on-hand of 10 then BOTH clear block-negative, whatever order the thread pool happens to run them
/// in. That is what turns
/// <see cref="MovementFoldRaceTests.Two_concurrent_issues_on_one_item_cannot_both_pass_block_negative"/>
/// from a detector that depends on scheduling into one that depends on construction.
///
/// WHY NOT AN UNCONDITIONAL RENDEZVOUS, the shape <see cref="FoldCompleteRendezvousLedger"/> is given in
/// this class's sibling test. The fold is inside the section the lock protects. Once the lock is real only
/// ONE racer can be in there; the other is parked on the semaphore and can never arrive, so a two-party
/// barrier placed there would strand the holder and die on its timeout. That is the same trap
/// <see cref="RendezvousItemWriteLock"/>'s remarks describe, and it is why the sibling — which runs on
/// <see cref="NoOpItemWriteLock"/> and therefore has no section — can use the plain rendezvous while this
/// one cannot.
///
/// WHY THE QUESTION IS "AM I HOLDING IT", NOT "IS ANYONE HOLDING IT". A global answer is a race: under the
/// mutation a sibling racer is transiently inside the lock between its acquire and its immediate dispose,
/// so a racer asking "is the lock held?" at its own fold could see that sibling's flicker and be let
/// through un-gated. "Am I holding it?" is decided entirely by the asking racer's own code path — held
/// through the body in the shipped build, released before the fold under the mutation — and has no window
/// at all. Each racer therefore gets its OWN <see cref="FoldOverlapRacer"/>, which is both its item write
/// lock and its ledger's fold-complete hook, over one shared gate.
///
/// The timeout is load-bearing for the reason it is on <see cref="RaceRendezvous"/> itself: if a future
/// change ever left ONE racer unprotected and the other protected, the unprotected one would wait alone
/// and must fail in seconds with a clear message rather than hang the suite.
/// </summary>
internal sealed class FoldOverlapGate(int racers, TimeSpan timeout)
{
    private readonly RaceRendezvous _allFolded = new(racers, timeout);
    private readonly HashSet<FoldOverlapRacer> _holding = [];   // reference identity — one entry per racer

    /// <summary>Seats one racer at the gate. Give the returned object to the service as its
    /// <see cref="IItemWriteLock"/> and its <see cref="FoldOverlapRacer.FoldCompletedAsync"/> to that
    /// racer's <see cref="FoldCompleteRendezvousLedger"/>, so the gate sees both halves of one racer.</summary>
    public FoldOverlapRacer NewRacer(IItemWriteLock inner) => new(this, inner);

    internal void Holding(FoldOverlapRacer racer, bool holding)
    {
        lock (_holding)
        {
            if (holding) _holding.Add(racer);
            else _holding.Remove(racer);
        }
    }

    internal Task FoldCompletedAsync(FoldOverlapRacer racer, CancellationToken ct)
    {
        // Protected — the lock has already made overlap impossible, so waiting for a racer that cannot
        // arrive would only strand this one.
        lock (_holding)
            if (_holding.Contains(racer)) return Task.CompletedTask;

        return _allFolded.ArriveAndWaitAsync(ct);
    }
}

/// <summary>One racer's seat at a <see cref="FoldOverlapGate"/>: an <see cref="IItemWriteLock"/> decorator
/// that tells the gate whether THIS racer currently holds the lock, plus the fold-complete hook that asks
/// the gate about it. The two halves must be the same instance — that is the identity the gate keys on.</summary>
internal sealed class FoldOverlapRacer(FoldOverlapGate gate, IItemWriteLock inner) : IItemWriteLock
{
    public async Task<IDisposable> AcquireAsync(Guid clientId, Guid itemId, CancellationToken ct = default)
    {
        IDisposable holder = await inner.AcquireAsync(clientId, itemId, ct);
        gate.Holding(this, true);
        return new Release(this, gate, holder);
    }

    /// <summary>Hook for this racer's ledger decorator, called the instant the fold's last read returns.</summary>
    public Task FoldCompletedAsync(CancellationToken ct = default) => gate.FoldCompletedAsync(this, ct);

    /// <summary>Clears the hold BEFORE releasing the real lock, so no racer can ever be admitted to the
    /// section while its predecessor still counts as holding. Disposing more than once is safe: the set
    /// remove is idempotent and <see cref="IItemWriteLock"/>'s contract already requires the inner holder
    /// to release only once.</summary>
    private sealed class Release(FoldOverlapRacer racer, FoldOverlapGate gate, IDisposable inner) : IDisposable
    {
        public void Dispose()
        {
            gate.Holding(racer, false);
            inner.Dispose();
        }
    }
}
