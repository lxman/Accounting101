# Inventory weighted-average fold race — design

**Date:** 2026-07-27
**Predecessor:** [2026-07-26-toctou-status-code-design.md](2026-07-26-toctou-status-code-design.md), whose
follow-ups doc left this as the only remaining problem in Inventory
**Module:** `Modules/Inventory/Accounting101.Inventory`

## What this closes

The TOCTOU follow-ups doc records one race here. Reading the code found **two**, and the recorded one is
the milder of them. Both are closed by the same mechanism; this spec names both so neither is mistaken
for the other later.

### Face 1 — same retry key (the recorded one)

Two callers share a movement id. Both pre-checks miss, so both take the create branch. The loser computed
its extended cost from a fold that now contains the winner's movement, and that cost was computed
**before** the store call — so the loser can throw block-negative (409), trip the `extendedCost <= 0`
guard, or post a divergent entry and take a ledger 422, **for a movement that actually landed**.

A false negative. It self-heals: the next retry's pre-check finds the movement, takes the replay branch,
and freezes the cost from the stored document.

### Face 2 — different keys, same item (not previously recorded)

Two concurrent movements on one item, distinct ids. Both read the fold `(OnHand 10, TotalValue 100)`.
Both issue 6. `InventoryValuation.Issue` guards with `quantity > current.OnHand` — **both pass**, because
neither movement is in the other's fold. Both create; `CreateWithIdAsync` arbitrates nothing, the ids
differ. Result: on-hand **−2**, carried value **−20**, and the second issue costed at a stale average.

**This does not self-heal**, and it defeats the exact invariant `Issue` exists to enforce. Face 1 produces
a wrong status code for an operation that succeeded; face 2 produces wrong numbers in the books and
nothing corrects them.

Verified absent: there is no lock, semaphore, optimistic-concurrency token, or per-item serialisation
anywhere in `Accounting101.Inventory`. The module's only atomicity is `CreateWithIdAsync` on a movement's
own `_id`.

## Two facts that shape the solution

**A movement is invisible to the fold until its entry is posted.** `ItemValuationService.ProjectOnHand`
counts a movement only when `OnBooks` finds an Active primary entry for it, and the value axis comes from
the ledger subledger, which has no lines until an entry exists. So `movements.RecordAsync` does **not**
move the fold — `ledger.PostAsync` does. Any critical section that stops at the store write leaves the
next caller reading a stale fold.

**`InventoryMovementService` is registered `Scoped`** (`InventoryServiceExtensions.cs:29`), a fresh
instance per request. The lock must therefore live in a singleton, not on the service.

## The mechanism

A per-item mutex, held across the whole write.

```csharp
public interface IItemWriteLock
{
    Task<IDisposable> AcquireAsync(Guid clientId, Guid itemId, CancellationToken ct = default);
}
```

Singleton implementation over a `ConcurrentDictionary<(Guid ClientId, Guid ItemId), SemaphoreSlim>`.
Registered in `InventoryServiceExtensions` beside the existing registrations. `InventoryMovementService`
stays `Scoped` and takes the lock as a constructor dependency.

**Taken at the very top of `RecordAsync`, before the existence pre-check.** That ordering is load-bearing:
it is what makes face 1 fall out for free. The loser now runs its pre-check *after* the winner has fully
completed, finds the movement, and takes the replay branch — which already freezes the cost from the
stored document. No special-casing, no re-probe-on-failure logic.

**Also taken in `VoidAsync`**, which changes the fold and could otherwise interleave with a record.

Released on exit including every throw path (`using`).

### Why not the alternatives

- **Narrowing the lock to fold-read → store-create** does not work, because of the visibility fact above:
  a second caller entering after the create still reads a stale fold. It would need post-hoc detection and
  compensating voids — touching money to fix a concurrency bug.
- **Re-probing the store when a state-dependent guard fires** (the originally recorded scope) fixes face 1
  only and leaves negative stock reachable. The lock subsumes it.

## What this deliberately does not claim

**It is correct for one process and silently useless across several.** An in-process semaphore is a
variable in one process's memory; two API instances each hold their own and neither sees the other's, so
two simultaneous writes landing on different instances would both proceed. That is worse than no lock,
because it looks like protection.

**Ratified 2026-07-27:** single-instance only, the sole consumer being the JordanSoft LLC books. This
constraint goes in a header comment on the lock class, in the same terms — naming the exact condition
that invalidates it and what replaces it: a compare-and-swap over `CreateWithIdAsync`, claiming a token
whose id encodes the fold version the caller read, so exactly one writer per version wins and losers
re-read and retry. That is the only distributed primitive the engine exposes to modules — `IDocumentStore`
offers `CreateWithIdAsync` and `NextNumberAsync` and no conditional update — and modules have no database
access to take a real lock.

**The guard reaches exactly as far as the lock does.** Any future write path that creates stock movements
without going through `InventoryMovementService` bypasses it.

## Error handling

No behaviour change. The lock only orders callers; every existing 409/422/exception path is untouched. A
caller that would have raced now waits and then reads a true fold, so block-negative fires when it should
and does not when it should not.

## Testing

`Accounting101.TestSupport.RaceRendezvous` with two participants, per the precedent the TOCTOU work set.
**Every race test must be watched failing against the un-fixed service** — that discipline is why the
predecessor's tests are worth anything, and a race test built on two real posts under `Task.WhenAll`
usually serialises by luck and passes against broken code.

- **Face 2 — the money bug.** Two concurrent Issues of 6 against an item holding 10, distinct ids. Before:
  both land, on-hand −2. After: one succeeds, the other takes block-negative, on-hand 4.
- **Face 1 — the false negative.** Two racers on one retry key. Before: the loser can throw for a movement
  that landed. After: exactly one reports created, the other replays, and both carry the same extended cost.
- **Concurrency preserved across items.** Two movements on *different* items proceed without serialising.
  Without this, a lock keyed too coarsely (per client, say) would pass both tests above while quietly
  serialising the entire module — a regression no other test would catch.

### Where the rendezvous goes, and why it is not where the precedent puts it

`RendezvousStockMovementStore` blocks **inside** the store's create — correct for the predecessor, whose
racers were never prevented from overlapping. That placement is wrong here and must not be copied.

Once the lock serialises callers, only one racer can be inside the critical section at a time, so a
second participant never arrives and `RaceRendezvous` throws `TimeoutException` after its timeout —
failing the request rather than deadlocking, but failing the test either way. **Every point inside the
critical section has this problem**, including the fold read.

The rendezvous therefore goes **immediately before the lock is acquired**: a test-only decorator on
`IItemWriteLock` whose `AcquireAsync` arrives at the rendezvous first, then delegates to the real lock.
This mirrors the `RendezvousStockMovementStore` decorator shape while sitting outside the section the
lock protects. Its job is only to guarantee the two callers are genuinely in flight at the same instant;
the lock then does the real work, and both racers still reach the rendezvous whether or not the lock
holds.

### The mutation that proves these tests can fail

Removing the lock entirely also removes the decorator's rendezvous, so the forced interleaving vanishes
and the test proves nothing. The mutation is instead **acquire but do not hold** — take the lock and
dispose it immediately rather than for the body's duration:

- Mutated: both racers arrive at the rendezvous, both are released, both immediately drop the semaphore,
  both read the same stale fold → on-hand −2. The face-2 test **fails**.
- Restored: both arrive and are released, then contend for the semaphore, and one waits → the second
  reads a true fold → block-negative → on-hand 4. Passes.

"Acquire but don't hold" is precisely the defect the design guards against, which is what makes it the
right mutation rather than an arbitrary one.

## Out of scope

- Multi-instance correctness (ratified above).
- The unbounded growth of the semaphore dictionary: one `SemaphoreSlim` per item ever written, never
  evicted. At this deployment's scale that is a handful of objects; eviction would need reference counting
  to avoid disposing a semaphore another caller is waiting on, which is not worth the risk here.
- Any change to `InventoryValuation`'s arithmetic, the movement model, or the posting composition.
