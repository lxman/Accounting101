# Inventory Fold Race Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop two concurrent writes to the same inventory item from both passing the block-negative guard and driving on-hand below zero.

**Architecture:** A singleton per-item mutex (`IItemWriteLock`), acquired at the top of both of `InventoryMovementService`'s write paths and held for their whole body — including the ledger post, because a movement is invisible to the valuation fold until its entry exists. Taken before the retry-key existence pre-check, which also fixes the same-key race for free: the loser's pre-check now runs after the winner has finished, so it takes the existing replay branch.

**Tech Stack:** .NET 10, xUnit, `Accounting101.TestSupport.RaceRendezvous`.

**Spec:** [`docs/superpowers/specs/2026-07-27-inventory-fold-race-design.md`](../specs/2026-07-27-inventory-fold-race-design.md)

## Global Constraints

- **Single-instance only, ratified.** An in-process semaphore is silently useless across processes. The lock class must carry a header comment saying so and naming the replacement (a CAS over `CreateWithIdAsync` keyed by fold version). Do not add multi-instance machinery.
- **No module logic may move into `Accounting101.Ledger.Api` or any shared shipped assembly.** The lock lives in `Accounting101.Inventory`.
- **No behaviour change to any existing guard.** The lock only orders callers; every existing 409/422/exception path stays exactly as it is.
- **Every race test must be watched failing** against the mutated service before it counts. The mandated mutation is "acquire but do not hold" — see Task 2.
- **`git add` explicit file lists** — never `git add -A`. `UI/Angular/src/app/core/api/environment.ts` carries a local dev edit that must stay uncommitted.
- **Tests:** `dotnet test Modules/Inventory/Accounting101.Inventory.Tests`

---

## File Structure

| File | Responsibility |
|---|---|
| `Modules/Inventory/Accounting101.Inventory/ItemWriteLock.cs` | **New** — `IItemWriteLock` + singleton `ItemWriteLock` |
| `Modules/Inventory/Accounting101.Inventory.Tests/ItemWriteLockTests.cs` | **New** — the primitive's own tests |
| `Modules/Inventory/Accounting101.Inventory/InventoryMovementService.cs` | Takes the lock in `RecordAsync` and `VoidAsync` |
| `Modules/Inventory/Accounting101.Inventory.Api/InventoryServiceExtensions.cs` | Registers the singleton |
| `Modules/Inventory/Accounting101.Inventory.Tests/RendezvousItemWriteLock.cs` | **New** — test decorator, rendezvous OUTSIDE the protected section |
| `Modules/Inventory/Accounting101.Inventory.Tests/NoOpItemWriteLock.cs` | **New** — grants immediately without exclusion, for store-level race tests |
| `Modules/Inventory/Accounting101.Inventory.Tests/FoldCompleteRendezvousLedger.cs` | **New** — rendezvous after the fold's LAST read, closing the torn-read window |
| `Modules/Inventory/Accounting101.Inventory.Tests/RecordingItemWriteLock.cs` | **New** — test spy recording which keys were acquired |
| `Modules/Inventory/Accounting101.Inventory.Tests/MovementFoldRaceTests.cs` | **New** — the two races + the cross-item concurrency guard |
| `Modules/Inventory/Accounting101.Inventory.Tests/InventoryMovementServiceTests.cs:21` | Constructor call gains the lock argument |
| `Modules/Inventory/Accounting101.Inventory.Tests/MovementRaceTests.cs:32` | Constructor call gains the lock argument |

---

## Task 1: The lock primitive

**Files:**
- Create: `Modules/Inventory/Accounting101.Inventory/ItemWriteLock.cs`
- Test: `Modules/Inventory/Accounting101.Inventory.Tests/ItemWriteLockTests.cs` (create)

**Interfaces:**
- Consumes: nothing.
- Produces: `IItemWriteLock.AcquireAsync(Guid clientId, Guid itemId, CancellationToken ct = default) → Task<IDisposable>` and the concrete `ItemWriteLock`, both used by Tasks 2 and 3.

- [ ] **Step 1: Write the failing tests**

Create `Modules/Inventory/Accounting101.Inventory.Tests/ItemWriteLockTests.cs`:

```csharp
namespace Accounting101.Inventory.Tests;

/// <summary>The per-item mutex behind the fold race fix. These are the primitive's own guarantees;
/// the service-level races that depend on them live in MovementFoldRaceTests.</summary>
public sealed class ItemWriteLockTests
{
    private static readonly Guid Client = Guid.NewGuid();

    [Fact]
    public async Task A_second_acquire_of_the_same_item_waits_for_the_first_to_release()
    {
        ItemWriteLock gate = new();
        Guid item = Guid.NewGuid();

        IDisposable first = await gate.AcquireAsync(Client, item);
        Task<IDisposable> second = gate.AcquireAsync(Client, item);

        Assert.False(second.IsCompleted);          // blocked while the first is held
        first.Dispose();
        IDisposable acquired = await second;       // released, so it completes
        acquired.Dispose();
    }

    [Fact]
    public async Task Different_items_do_not_block_each_other()
    {
        ItemWriteLock gate = new();

        IDisposable a = await gate.AcquireAsync(Client, Guid.NewGuid());
        IDisposable b = await gate.AcquireAsync(Client, Guid.NewGuid());   // must not hang

        a.Dispose();
        b.Dispose();
    }

    [Fact]
    public async Task The_same_item_under_a_different_client_does_not_block()
    {
        ItemWriteLock gate = new();
        Guid item = Guid.NewGuid();

        IDisposable a = await gate.AcquireAsync(Guid.NewGuid(), item);
        IDisposable b = await gate.AcquireAsync(Guid.NewGuid(), item);     // must not hang

        a.Dispose();
        b.Dispose();
    }

    /// <summary>A double dispose must not over-release the semaphore — that would raise its count above
    /// one and silently destroy mutual exclusion for every later caller.</summary>
    [Fact]
    public async Task Disposing_twice_does_not_over_release()
    {
        ItemWriteLock gate = new();
        Guid item = Guid.NewGuid();

        IDisposable held = await gate.AcquireAsync(Client, item);
        held.Dispose();
        held.Dispose();

        IDisposable one = await gate.AcquireAsync(Client, item);
        Task<IDisposable> two = gate.AcquireAsync(Client, item);
        Assert.False(two.IsCompleted);             // still mutually exclusive

        one.Dispose();
        (await two).Dispose();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Modules/Inventory/Accounting101.Inventory.Tests --filter ItemWriteLockTests`
Expected: compile failure — `ItemWriteLock` does not exist.

- [ ] **Step 3: Write the implementation**

Create `Modules/Inventory/Accounting101.Inventory/ItemWriteLock.cs`:

```csharp
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
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Modules/Inventory/Accounting101.Inventory.Tests --filter ItemWriteLockTests`
Expected: PASS, 4 tests.

- [ ] **Step 5: Commit**

```bash
git add Modules/Inventory/Accounting101.Inventory/ItemWriteLock.cs \
        Modules/Inventory/Accounting101.Inventory.Tests/ItemWriteLockTests.cs
git commit -m "feat(inventory): a per-item write lock"
```

---

## Task 2: Serialise the record path

**Files:**
- Modify: `Modules/Inventory/Accounting101.Inventory/InventoryMovementService.cs` (constructor; `RecordAsync`)
- Modify: `Modules/Inventory/Accounting101.Inventory.Api/InventoryServiceExtensions.cs`
- Modify: `Modules/Inventory/Accounting101.Inventory.Tests/InventoryMovementServiceTests.cs:21`
- Modify: `Modules/Inventory/Accounting101.Inventory.Tests/MovementRaceTests.cs:32`
- Create: `Modules/Inventory/Accounting101.Inventory.Tests/RendezvousItemWriteLock.cs`
- Test: `Modules/Inventory/Accounting101.Inventory.Tests/MovementFoldRaceTests.cs` (create)

**Interfaces:**
- Consumes: `IItemWriteLock` / `ItemWriteLock` from Task 1.
- Produces: `InventoryMovementService`'s constructor gains a sixth parameter, `IItemWriteLock itemLock`, appended after `ItemValuationService valuation`. Task 3 modifies the same class.

### Background the implementer needs

The two existing test files construct the service directly, so both break the moment the constructor changes. That is expected; the plan updates both.

**The rendezvous must sit OUTSIDE the section the lock protects.** `RendezvousStockMovementStore` (the predecessor's decorator) blocks inside the store's probe, which is inside the critical section — once callers serialise, the second participant never arrives and `RaceRendezvous` throws `TimeoutException`. Do not copy that placement for the new tests. The new decorator arrives at the rendezvous and then delegates to the real lock, so both racers reach it whether or not the lock holds.

**A rendezvous decorator fires on every acquire, including seeding calls.** A test that seeds stock through the same service instance would strand the seed at a 2-participant rendezvous and time out. Seed through a second service instance that shares the same stores but wraps the *undecorated* lock.

**Two guarantees, tested separately — do not conflate them.** The store's atomic-create bool is the *portable* arbiter of created-vs-replayed: it holds even across processes, where an in-process lock does not. The lock is the single-instance addition on top. They need independent tests, and the store-level ones must bypass the lock to stay reachable at all:

- Any test whose rendezvous blocks **inside** the critical section (i.e. `RendezvousStockMovementStore`, which blocks in the store probe) must build its service with `NoOpItemWriteLock`. With the real lock, racer A holds it and waits at the rendezvous while B waits on the lock and never arrives — A then dies on `RaceRendezvous`'s timeout. This applies to the pre-existing `MovementRaceTests` too, which is why its construction site takes the no-op lock rather than the real one.
- Only the face-2 test uses the real lock, and its rendezvous sits outside the protected section.

- [ ] **Step 1: Write the test decorator**

Create `Modules/Inventory/Accounting101.Inventory.Tests/RendezvousItemWriteLock.cs`:

```csharp
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
```

Create `Modules/Inventory/Accounting101.Inventory.Tests/FoldCompleteRendezvousLedger.cs`:

```csharp
using Accounting101.Ledger.Contracts;
using Accounting101.TestSupport;

namespace Accounting101.Inventory.Tests;

/// <summary>
/// Rendezvouses after the fold's LAST read, so no racer starts writing until every racer has finished
/// reading the whole valuation.
///
/// The fold is THREE reads, not one: <see cref="ItemValuationService.GetAsync"/> takes value from
/// <c>GetSubledgerAsync</c>, then quantity from <c>ProjectQuantityAsync</c>, which reads the movement
/// store and then <c>GetEntriesBySourceRefsAsync</c>. Nothing holds them together when the item write
/// lock is bypassed, so a racer preempted mid-fold can resume with a stale value and a fresh quantity —
/// an incoherent average, a different extended cost, and a divergent-content rejection from the ledger
/// fake. Blocking on the last of the three closes that window WITHOUT serialising the racers, which is
/// exactly what a test that must reach the un-serialised interleaving needs.
///
/// Only <c>GetEntriesBySourceRefsAsync</c> is intercepted; everything else delegates untouched.
/// </summary>
internal sealed class FoldCompleteRendezvousLedger(ILedgerClient inner, RaceRendezvous rendezvous) : ILedgerClient
{
    public async Task<IReadOnlyList<EntryResponse>> GetEntriesBySourceRefsAsync(
        Guid clientId, IReadOnlyList<Guid> sourceRefs, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<EntryResponse> entries = await inner.GetEntriesBySourceRefsAsync(clientId, sourceRefs, cancellationToken);
        await rendezvous.ArriveAndWaitAsync(cancellationToken);   // this caller's fold is now complete
        return entries;
    }

    public Task<PostEntryResponse> PostAsync(Guid clientId, PostEntryRequest entry, CancellationToken cancellationToken = default) =>
        inner.PostAsync(clientId, entry, cancellationToken);

    public Task<EntryResponse> ReverseAsync(Guid clientId, Guid entryId, ReverseRequest request, CancellationToken cancellationToken = default) =>
        inner.ReverseAsync(clientId, entryId, request, cancellationToken);

    public Task<EntryResponse> VoidAsync(Guid clientId, Guid entryId, VoidRequest request, CancellationToken cancellationToken = default) =>
        inner.VoidAsync(clientId, entryId, request, cancellationToken);

    public Task<IReadOnlyList<EntryResponse>> GetEntriesBySourceRefAsync(Guid clientId, Guid sourceRef, CancellationToken cancellationToken = default) =>
        inner.GetEntriesBySourceRefAsync(clientId, sourceRef, cancellationToken);

    public Task<IReadOnlyList<SubledgerLineResponse>> GetSubledgerAsync(
        Guid clientId, Guid account, string dimension, DateOnly? asOf, CancellationToken cancellationToken = default,
        bool includePending = false) =>
        inner.GetSubledgerAsync(clientId, account, dimension, asOf, cancellationToken, includePending);

    public Task<IReadOnlyList<AccountResponse>> GetAccountsAsync(Guid clientId, CancellationToken cancellationToken = default) =>
        inner.GetAccountsAsync(clientId, cancellationToken);

    public Task<CapabilitiesResponse> GetMyCapabilitiesAsync(Guid clientId, CancellationToken cancellationToken = default) =>
        inner.GetMyCapabilitiesAsync(clientId, cancellationToken);
}
```

Create `Modules/Inventory/Accounting101.Inventory.Tests/NoOpItemWriteLock.cs`:

```csharp
namespace Accounting101.Inventory.Tests;

/// <summary>
/// Grants immediately and excludes nobody — for tests that must still be able to drive two callers into
/// the same critical section.
///
/// The store's insert-only create is the arbiter of created-vs-replayed, and that guarantee is the
/// PORTABLE one: it holds even across processes, where <see cref="ItemWriteLock"/> does not. Tests that
/// pin it therefore have to bypass the lock, because with the lock in place the interleaving they need
/// is unreachable through the service — and worse, a rendezvous blocking inside the critical section
/// would strand its second participant and die on RaceRendezvous's timeout.
/// </summary>
internal sealed class NoOpItemWriteLock : IItemWriteLock
{
    private static readonly IDisposable Nothing = new NoRelease();

    public Task<IDisposable> AcquireAsync(Guid clientId, Guid itemId, CancellationToken ct = default) =>
        Task.FromResult(Nothing);

    private sealed class NoRelease : IDisposable
    {
        public void Dispose() { }
    }
}
```

- [ ] **Step 2: Write the failing race tests**

Create `Modules/Inventory/Accounting101.Inventory.Tests/MovementFoldRaceTests.cs`:

```csharp
using Accounting101.TestSupport;

namespace Accounting101.Inventory.Tests;

/// <summary>
/// The weighted-average fold race, both faces, pinned deterministically.
///
/// WHAT MAKES THESE VALID: change the service's acquire to release immediately —
/// <c>(await itemLock.AcquireAsync(clientId, request.ItemId, ct)).Dispose();</c> instead of holding it
/// with <c>using</c> — and the first two tests fail. Removing the lock outright is NOT a valid mutation:
/// it removes the rendezvous with it, so the racers no longer overlap and the tests prove nothing.
/// </summary>
public sealed class MovementFoldRaceTests
{
    private sealed record Rig(
        InventoryMovementService Seeder, InventoryMovementService Racing,
        InMemoryItemStore Items, ItemValuationService Valuation);

    /// <summary>Two services over ONE set of stores: the seeder holds the undecorated lock so setup calls
    /// do not strand at the rendezvous, the racer holds the decorated one. They share the same underlying
    /// ItemWriteLock, so seeding and racing are still mutually exclusive.</summary>
    private static Rig Build(RaceRendezvous rendezvous)
    {
        InMemoryItemStore items = new();
        InMemoryStockMovementStore movements = new();
        FakeLedgerClient ledger = new();
        FixedInventoryAccountsProvider accounts = new();
        ItemValuationService valuation = new(movements, accounts, ledger);
        ItemWriteLock realLock = new();
        return new Rig(
            new InventoryMovementService(items, movements, accounts, ledger, valuation, realLock),
            new InventoryMovementService(items, movements, accounts, ledger, valuation,
                new RendezvousItemWriteLock(realLock, rendezvous)),
            items, valuation);
    }

    private static async Task<Item> SeedTenOnHandAsync(Rig rig, Guid clientId)
    {
        Item item = await rig.Items.CreateAsync(clientId, new ItemBody("SKU1", "Widget", null, "each"));
        await rig.Seeder.RecordAsync(clientId, new RecordMovement(
            item.Id, MovementType.Receipt, 10m, 2m, new DateOnly(2026, 1, 15), "seed"));
        return item;
    }

    /// <summary>FACE 2, the money bug. Both racers read (OnHand 10, Value 100), both issue 6, and
    /// InventoryValuation.Issue's <c>quantity > current.OnHand</c> guard passes for BOTH because neither
    /// movement is in the other's fold. Unfixed, on-hand lands at -2 and stays there.</summary>
    [Fact]
    public async Task Two_concurrent_issues_on_one_item_cannot_both_pass_block_negative()
    {
        RaceRendezvous rendezvous = new(participants: 2, timeout: TimeSpan.FromSeconds(5));
        Rig rig = Build(rendezvous);
        Guid clientId = Guid.NewGuid();
        Item item = await SeedTenOnHandAsync(rig, clientId);

        Task<(StockMovement Movement, bool Replayed)> a = rig.Racing.RecordAsync(clientId, new RecordMovement(
            item.Id, MovementType.Issue, 6m, null, new DateOnly(2026, 1, 16), "issue A", Guid.NewGuid()));
        Task<(StockMovement Movement, bool Replayed)> b = rig.Racing.RecordAsync(clientId, new RecordMovement(
            item.Id, MovementType.Issue, 6m, null, new DateOnly(2026, 1, 16), "issue B", Guid.NewGuid()));

        int succeeded = 0, refused = 0;
        foreach (Task<(StockMovement Movement, bool Replayed)> t in new[] { a, b })
        {
            try { await t; succeeded++; }
            catch (InvalidOperationException) { refused++; }   // "Issue would drive on-hand below zero."
        }

        Assert.Equal(1, succeeded);
        Assert.Equal(1, refused);
        ItemValuation after = await rig.Valuation.GetAsync(clientId, item.Id, includePending: true);
        Assert.Equal(4m, after.OnHand);          // 10 - 6, never 10 - 6 - 6
    }

    /// <summary>
    /// FACE 1, on an ISSUE — the type MovementRaceTests deliberately avoids, extending that test's
    /// guarantee to the fold-costed path. Two racers share a retry key and BOTH get past the probe;
    /// the store's insert-only create is what makes exactly one of them a create.
    ///
    /// DELIBERATELY BUILT WITH NoOpItemWriteLock. The interleaving this pins is unreachable through the
    /// service once the real lock is in place — and the rendezvous, which blocks inside the store probe
    /// and therefore inside the critical section, would strand its second participant and die on
    /// RaceRendezvous's timeout. What it pins is the PORTABLE guarantee, the one that survives where an
    /// in-process lock would not.
    ///
    /// QUANTITIES ARE LOAD-BEARING: 3 against a seed of 10, deliberately small enough that BOTH racers
    /// clear block-negative under every ordering. Running unprotected (that is the point — see above),
    /// a loser whose fold read lands after the winner's write sees the reduced on-hand; with 6 against
    /// 10 it would see 4, fail block-negative, and throw. That throw is face 1's bug, so asserting
    /// against it here would be asserting protected behaviour from unprotected code. At 3 the guard is
    /// unreachable either way, which isolates the store-bool guarantee from the block-negative guard.
    ///
    /// The extended cost is identical under both orderings, so no divergent-content rejection can fire
    /// either: issuing at the weighted average leaves the average unchanged (10 units / 20.00 → avg 2.00;
    /// after one issue of 3, 7 units / 14.00 → still avg 2.00), so both racers compose 6.00 whichever
    /// fold they read. Do not "simplify" these numbers.
    ///
    /// TWO RENDEZVOUS, and both are needed. The fold is three reads, not one — value from
    /// GetSubledgerAsync, then quantity from a movement-store read and GetEntriesBySourceRefsAsync — so
    /// "whichever fold they read" is only true if a racer's fold is not TORN across the other's write.
    /// Unguarded, a racer preempted after the value read resumes with a stale value (20) and a fresh
    /// quantity (7), giving avg 2.857 and a cost of 8.57 instead of 6.00, and the ledger fake rejects it
    /// as divergent content under the winner's SourceRef.
    ///   - `atProbe` (in the store probe) puts both racers past the existence check before either creates.
    ///   - `afterFold` (after the fold's LAST read) puts both past the ENTIRE valuation before either
    ///     writes, which is what makes the torn read impossible.
    /// Neither serialises the racers, so the un-locked interleaving this test exists to pin is still
    /// reached. Removing `afterFold` reintroduces a narrow, real flake.
    ///
    /// WHAT MAKES IT VALID: delete the service's <c>replayed = !result.Created;</c> line and both racers
    /// claim a create, so the single-create assertion sees two and fails.
    /// </summary>
    [Fact]
    public async Task Two_issue_racers_on_one_retry_key_yield_one_create_and_one_replay()
    {
        InMemoryItemStore items = new();
        InMemoryStockMovementStore inner = new();
        FakeLedgerClient ledger = new();
        FixedInventoryAccountsProvider accounts = new();

        // Seeder: undecorated on BOTH seams, so setup can never arrive at either rendezvous and strand
        // it. (The seed would in fact miss the fold rendezvous anyway — ProjectQuantityAsync returns
        // early when the item has no movements yet, so GetEntriesBySourceRefsAsync is never reached —
        // but relying on that accident would break the moment seeding order changed.)
        ItemValuationService seedValuation = new(inner, accounts, ledger);
        InventoryMovementService seeder = new(items, inner, accounts, ledger, seedValuation, new NoOpItemWriteLock());

        RaceRendezvous atProbe = new(participants: 2, timeout: TimeSpan.FromSeconds(5));
        RaceRendezvous afterFold = new(participants: 2, timeout: TimeSpan.FromSeconds(5));
        RendezvousStockMovementStore racingStore = new(inner, atProbe);              // blocks in the PROBE
        FoldCompleteRendezvousLedger racingLedger = new(ledger, afterFold);          // blocks after the FOLD
        ItemValuationService raceValuation = new(racingStore, accounts, racingLedger);
        InventoryMovementService racing = new(items, racingStore, accounts, racingLedger, raceValuation, new NoOpItemWriteLock());

        Guid clientId = Guid.NewGuid();
        Item item = await items.CreateAsync(clientId, new ItemBody("SKU1", "Widget", null, "each"));

        await seeder.RecordAsync(clientId, new RecordMovement(
            item.Id, MovementType.Receipt, 10m, 2m, new DateOnly(2026, 1, 15), "seed"));

        Guid key = Guid.NewGuid();
        RecordMovement request = new(
            item.Id, MovementType.Issue, 3m, null, new DateOnly(2026, 1, 16), "issue", key);

        (StockMovement Movement, bool Replayed)[] results =
            await Task.WhenAll(racing.RecordAsync(clientId, request), racing.RecordAsync(clientId, request));

        Assert.Single(results, r => !r.Replayed);
        Assert.Single(results, r => r.Replayed);
        Assert.All(results, r => Assert.Equal(key, r.Movement.Id));

        // Read back through the UNDECORATED valuation — the racing one would arrive at afterFold again.
        ItemValuation after = await seedValuation.GetAsync(clientId, item.Id, includePending: true);
        Assert.Equal(7m, after.OnHand);          // 10 - 3: the movement applied exactly once, not twice
    }

    /// <summary>The lock must be keyed per ITEM, not per client. Keyed too coarsely it would still pass
    /// both tests above while quietly serialising the whole module, and nothing else would catch it.
    /// Here the rendezvous sits INSIDE the critical section on purpose: two writes to different items must
    /// be able to occupy their sections at once, so both arrive and no timeout is thrown.</summary>
    [Fact]
    public async Task Writes_to_different_items_are_not_serialised()
    {
        InMemoryItemStore items = new();
        InMemoryStockMovementStore movements = new();
        RaceRendezvous rendezvous = new(participants: 2, timeout: TimeSpan.FromSeconds(5));
        RendezvousStockMovementStore racingStore = new(movements, rendezvous);   // blocks in the PROBE
        FakeLedgerClient ledger = new();
        FixedInventoryAccountsProvider accounts = new();
        ItemValuationService valuation = new(racingStore, accounts, ledger);
        InventoryMovementService service = new(items, racingStore, accounts, ledger, valuation, new ItemWriteLock());

        Guid clientId = Guid.NewGuid();
        Item one = await items.CreateAsync(clientId, new ItemBody("SKU1", "Widget", null, "each"));
        Item two = await items.CreateAsync(clientId, new ItemBody("SKU2", "Gadget", null, "each"));

        // Explicit ids: the probe the rendezvous lives in only runs when a retry key is supplied.
        Task<(StockMovement Movement, bool Replayed)> a = service.RecordAsync(clientId, new RecordMovement(
            one.Id, MovementType.Receipt, 5m, 1m, new DateOnly(2026, 1, 15), "A", Guid.NewGuid()));
        Task<(StockMovement Movement, bool Replayed)> b = service.RecordAsync(clientId, new RecordMovement(
            two.Id, MovementType.Receipt, 5m, 1m, new DateOnly(2026, 1, 15), "B", Guid.NewGuid()));

        // The liveness check is that this does NOT throw TimeoutException — if the lock were keyed per
        // client instead of per item, the second racer could never reach the rendezvous. The assertions
        // below additionally pin that both writes really landed, so the test cannot pass vacuously.
        (StockMovement Movement, bool Replayed)[] results = await Task.WhenAll(a, b);

        Assert.All(results, r => Assert.False(r.Replayed));        // two distinct creates, not a replay
        Assert.Equal(2, results.Select(r => r.Movement.Id).Distinct().Count());
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test Modules/Inventory/Accounting101.Inventory.Tests --filter MovementFoldRaceTests`
Expected: compile failure — the service constructor takes five arguments, not six.

- [ ] **Step 4: Add the lock to the service's record path**

In `Modules/Inventory/Accounting101.Inventory/InventoryMovementService.cs`, change the primary constructor (line 18-20) to:

```csharp
public sealed class InventoryMovementService(
    IItemStore items, IStockMovementStore movements, IInventoryAccountsProvider accounts, ILedgerClient ledger,
    ItemValuationService valuation, IItemWriteLock itemLock)
```

In `RecordAsync`, immediately after the `ValidateShape(request);` line and before `Guid id = request.Id ?? Guid.NewGuid();`, insert:

```csharp
        // Serialise every write to this item's stock position, for the whole body. Two things make this
        // the right boundary:
        //
        // 1. It must be taken BEFORE the existence pre-check. That is what makes a same-key racer take the
        //    replay branch below rather than computing a cost from a fold the winner has already moved.
        // 2. It must be held THROUGH the ledger post. A movement is invisible to the fold until its entry
        //    exists (ItemValuationService.OnBooks requires one, and the value axis comes from the
        //    subledger), so releasing after the store write would let the next caller read a stale fold.
        //
        // Single-instance only — see ItemWriteLock's remarks.
        using IDisposable itemGate = await itemLock.AcquireAsync(clientId, request.ItemId, ct);
```

- [ ] **Step 5: Register the singleton**

In `Modules/Inventory/Accounting101.Inventory.Api/InventoryServiceExtensions.cs`, immediately after the `services.AddScoped<InventoryMovementService>();` line, add:

```csharp
        // Singleton on purpose: InventoryMovementService is Scoped, so a per-request lock would guard
        // nothing. Single-instance only — see ItemWriteLock's remarks.
        services.AddSingleton<IItemWriteLock, ItemWriteLock>();
```

- [ ] **Step 6: Update the two existing construction sites**

In `Modules/Inventory/Accounting101.Inventory.Tests/InventoryMovementServiceTests.cs`, change line 21 to:

```csharp
        InventoryMovementService service = new(items, movements, accounts, ledger, valuation, new ItemWriteLock());
```

In `Modules/Inventory/Accounting101.Inventory.Tests/MovementRaceTests.cs`, change line 32 to take the **no-op** lock — this test's rendezvous blocks inside the store probe, which the real lock now wraps, so with the real lock its second racer could never arrive and it would die on the rendezvous timeout:

```csharp
        // NoOp on purpose: this test pins the STORE's insert-only create as the arbiter of
        // created-vs-replayed — the guarantee that holds even where an in-process lock would not. Its
        // rendezvous blocks inside the critical section, so the real lock would strand the second racer.
        InventoryMovementService service = new(items, racingStore, accounts, ledger, valuation, new NoOpItemWriteLock());
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test Modules/Inventory/Accounting101.Inventory.Tests --filter MovementFoldRaceTests`
Expected: PASS, 3 tests.

Then the whole module: `dotnet test Modules/Inventory/Accounting101.Inventory.Tests`
Expected: PASS, no regressions. `MovementRaceTests` in particular must still pass — the lock does not change its outcome, only the path it takes.

- [ ] **Step 8: Prove the race tests can fail (mandatory mutation)**

Temporarily replace the `using IDisposable itemGate = ...` line from Step 4 with the acquire-but-don't-hold form:

```csharp
        (await itemLock.AcquireAsync(clientId, request.ItemId, ct)).Dispose();
```

Run: `dotnet test Modules/Inventory/Accounting101.Inventory.Tests --filter MovementFoldRaceTests`
Expected: `Two_concurrent_issues_on_one_item_cannot_both_pass_block_negative` FAILS — both issues succeed and on-hand lands at −2.

The other two are unaffected by this mutation **by design**, and that is not a defect: `Two_issue_racers_on_one_retry_key_yield_one_create_and_one_replay` builds its service with `NoOpItemWriteLock`, so it never touches the mutated line, and `Writes_to_different_items_are_not_serialised` is a liveness test. Each has its own mutation, recorded in its class remarks:

- Face 1 — delete the service's `replayed = !result.Created;` line; both racers then claim a create and its single-create assertion fails.
- Cross-item — key the lock on `clientId` alone instead of `(clientId, itemId)`; the second write can then never enter its critical section and the rendezvous times out.

Run the face-1 mutation too, so both race tests are proven capable of failing. Restore everything and re-run: all 3 pass. Do not commit any mutation. Report the failing output for both.

- [ ] **Step 9: Commit**

```bash
git add Modules/Inventory/Accounting101.Inventory/InventoryMovementService.cs \
        Modules/Inventory/Accounting101.Inventory.Api/InventoryServiceExtensions.cs \
        Modules/Inventory/Accounting101.Inventory.Tests/RendezvousItemWriteLock.cs \
        Modules/Inventory/Accounting101.Inventory.Tests/NoOpItemWriteLock.cs \
        Modules/Inventory/Accounting101.Inventory.Tests/FoldCompleteRendezvousLedger.cs \
        Modules/Inventory/Accounting101.Inventory.Tests/MovementFoldRaceTests.cs \
        Modules/Inventory/Accounting101.Inventory.Tests/InventoryMovementServiceTests.cs \
        Modules/Inventory/Accounting101.Inventory.Tests/MovementRaceTests.cs
git commit -m "fix(inventory): serialise concurrent writes to one item's stock position"
```

---

## Task 3: Serialise the void path

**Files:**
- Modify: `Modules/Inventory/Accounting101.Inventory/InventoryMovementService.cs` (`VoidAsync`)
- Create: `Modules/Inventory/Accounting101.Inventory.Tests/RecordingItemWriteLock.cs`
- Test: `Modules/Inventory/Accounting101.Inventory.Tests/MovementFoldRaceTests.cs` (append)

**Interfaces:**
- Consumes: `IItemWriteLock` from Task 1; the six-argument `InventoryMovementService` constructor from Task 2.
- Produces: nothing consumed by later tasks.

### Background the implementer needs

`VoidAsync` takes a `movementId`, not an item id, and the lock is keyed by item — so it must read the movement to learn the key before it can acquire. That pre-read is for the key only; every existing check still runs against a re-read taken under the lock, so nothing is decided on a value read outside it.

- [ ] **Step 1: Write the failing tests**

Create `Modules/Inventory/Accounting101.Inventory.Tests/RecordingItemWriteLock.cs`:

```csharp
using System.Collections.Concurrent;

namespace Accounting101.Inventory.Tests;

/// <summary>Records which (client, item) keys were acquired, so a test can assert a write path locks the
/// right item. Delegates to a real lock so behaviour is unchanged.</summary>
internal sealed class RecordingItemWriteLock(IItemWriteLock inner) : IItemWriteLock
{
    private readonly ConcurrentQueue<(Guid ClientId, Guid ItemId)> _acquired = new();

    public IReadOnlyList<(Guid ClientId, Guid ItemId)> Acquired => _acquired.ToList();

    public async Task<IDisposable> AcquireAsync(Guid clientId, Guid itemId, CancellationToken ct = default)
    {
        _acquired.Enqueue((clientId, itemId));
        return await inner.AcquireAsync(clientId, itemId, ct);
    }
}
```

Append to `MovementFoldRaceTests`, inside the existing class:

```csharp
    /// <summary>Both write paths must lock the item they are about to move. A void that did not lock could
    /// interleave with a record and let it compute from a fold mid-void.</summary>
    [Fact]
    public async Task Recording_and_voiding_both_acquire_the_lock_for_the_movements_item()
    {
        InMemoryItemStore items = new();
        InMemoryStockMovementStore movements = new();
        FakeLedgerClient ledger = new();
        FixedInventoryAccountsProvider accounts = new();
        ItemValuationService valuation = new(movements, accounts, ledger);
        RecordingItemWriteLock gate = new(new ItemWriteLock());
        InventoryMovementService service = new(items, movements, accounts, ledger, valuation, gate);

        Guid clientId = Guid.NewGuid();
        Item item = await items.CreateAsync(clientId, new ItemBody("SKU1", "Widget", null, "each"));

        (StockMovement Movement, bool Replayed) recorded = await service.RecordAsync(clientId, new RecordMovement(
            item.Id, MovementType.Receipt, 10m, 2m, new DateOnly(2026, 1, 15), "seed"));
        Assert.Equal((clientId, item.Id), Assert.Single(gate.Acquired));      // the record path locked the item

        await service.VoidAsync(clientId, recorded.Movement.Id, "undo");
        Assert.Equal(2, gate.Acquired.Count);                                 // and so did the void
        Assert.All(gate.Acquired, key => Assert.Equal((clientId, item.Id), key));
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Modules/Inventory/Accounting101.Inventory.Tests --filter Recording_and_voiding_both_acquire`
Expected: FAIL — one acquisition recorded, not two, because `VoidAsync` does not take the lock yet.

- [ ] **Step 3: Add the lock to the void path**

In `VoidAsync`, replace the opening two lines:

```csharp
        StockMovement movement = await movements.GetAsync(clientId, movementId, ct)
            ?? throw new KeyNotFoundException($"Stock movement {movementId} not found.");
```

with:

```csharp
        // The lock is keyed by ITEM and only the movement knows its item, so read it once to learn the
        // key. This pre-read decides nothing: every check below runs against the re-read taken under the
        // lock. Single-instance only — see ItemWriteLock's remarks.
        StockMovement forKey = await movements.GetAsync(clientId, movementId, ct)
            ?? throw new KeyNotFoundException($"Stock movement {movementId} not found.");
        using IDisposable itemGate = await itemLock.AcquireAsync(clientId, forKey.ItemId, ct);

        StockMovement movement = await movements.GetAsync(clientId, movementId, ct)
            ?? throw new KeyNotFoundException($"Stock movement {movementId} not found.");
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test Modules/Inventory/Accounting101.Inventory.Tests --filter Recording_and_voiding_both_acquire`
Expected: PASS.

- [ ] **Step 5: Run the module suite**

Run: `dotnet test Modules/Inventory/Accounting101.Inventory.Tests`
Expected: PASS, no regressions.

- [ ] **Step 6: Commit**

```bash
git add Modules/Inventory/Accounting101.Inventory/InventoryMovementService.cs \
        Modules/Inventory/Accounting101.Inventory.Tests/RecordingItemWriteLock.cs \
        Modules/Inventory/Accounting101.Inventory.Tests/MovementFoldRaceTests.cs
git commit -m "fix(inventory): serialise the void path against concurrent records"
```

---

## Task 3b: Pin the void path's HOLD property

**Added after Task 3's review.** Task 3's spy test proves `VoidAsync` CALLS `AcquireAsync` with the
right item key. It does not prove the void HOLDS the lock: it passes unchanged under the plan's
mandated `(await itemLock.AcquireAsync(...)).Dispose();` mutation, and would pass even if the
acquisition moved to the method's last line. Holding is the property that actually serialises a void
against a concurrent record, and no test pins it. Human ruling: close it, do not document-and-accept —
the same call as the Task-2 torn-read residual.

**Files:**
- Create: one test double under `Modules/Inventory/Accounting101.Inventory.Tests/`
- Test: `Modules/Inventory/Accounting101.Inventory.Tests/MovementFoldRaceTests.cs` (append)
- **No production file changes.** `VoidAsync` is already correct; this task adds coverage only. If you
  believe production code must change to make this testable, STOP and report — that is a finding, not
  a licence to edit.

### Why this task deliberately does not hand you the test code

Every one of Task 2's five review rounds was a plan-authored test defect — test code written away from
the code it tests. This task gives you the requirement, the verified seam facts, and the constraints.
You design the test against the actual code and you own its validity.

### The requirement

One test that fails under the acquire-but-don't-hold mutation and passes as written. It must
demonstrate that while `VoidAsync` is mid-body on an item, a concurrent `RecordAsync` on that same item
cannot proceed.

### Verified seam facts (checked live at 2026-07-27; re-confirm, don't just trust)

- `ILedgerClient.GetEntriesBySourceRefAsync` (SINGULAR) is called by `VoidAsync`, unconditionally,
  inside the critical section — it is the void's entry lookup. The valuation fold uses the PLURAL
  `GetEntriesBySourceRefsAsync` instead (see `FoldCompleteRendezvousLedger`'s remarks). Verify that the
  record path never reaches the singular method before you rely on this.
- `FoldCompleteRendezvousLedger` is the worked example of an `ILedgerClient` decorator that intercepts
  exactly one method and delegates the other eight untouched. Follow its shape.
- `MovementFoldRaceTests.Build` returns a `Rig` with a SEEDER service on the undecorated lock and a
  RACING service on the decorated one, over one shared set of stores. Setup calls must go through the
  seeder or they strand. Your test may need its own variant of this rig.

### Constraints, each one paid for by a failed round in Task 2

- **A two-party rendezvous is the wrong shape here.** Only ONE party is ever inside the void's critical
  section; the other is blocked at the lock and will never arrive. A rendezvous that waits for a second
  arrival inside that section deadlocks and dies on its timeout. You want a one-sided gate the TEST
  thread opens.
- **Nothing that needs the item lock may block inside the section**, for the same reason.
- **Decide what your assertion can still OBSERVE once the lock is in place** before you write it. An
  assertion that only holds because the fix destroyed the interleaving it needed proves nothing.
- **Watch it fail.** Apply the mutation to `VoidAsync`, run the test, record the actual failure output
  in your report, then revert the mutation. A hold test that has not been seen failing does not count.

- [ ] **Step 1: Design and write the test, watch it fail under the mutation**
- [ ] **Step 2: Revert the mutation, confirm it passes**
- [ ] **Step 3: Run the full module suite** (`dotnet test Modules/Inventory/Accounting101.Inventory.Tests`) — 118 currently green, expect 119
- [ ] **Step 4: Update `MovementFoldRaceTests`' class remarks** — they are a coverage map for the
  acquire-but-don't-hold mutation, they currently say "the other three tests below" and claim each test
  names its own mutation, and your test makes both statements false again. Task 3's review round burned
  on exactly this. Re-read every quantified claim in that block after you add your test.
- [ ] **Step 5: Commit** with an explicit `git add` file list (never `-A`;
  `UI/Angular/src/app/core/api/environment.ts` stays uncommitted)

---

## Task 3c: Make the flagship race test detect its mutation deterministically

**Added after Task 3b's review, which confirmed the defect empirically.**
`Two_concurrent_issues_on_one_item_cannot_both_pass_block_negative` is this branch's central proof —
the test that shows two concurrent issues cannot both clear block-negative. It detects its mandated
record-path mutation (`(await itemLock.AcquireAsync(clientId, request.ItemId, ct)).Dispose();` in
`RecordAsync`) **only when it happens to run first in its class out of a cold thread pool.** xUnit
orders test methods by a hash of their names, not by source position, so that is scheduling luck, not
construction. The reviewer's matrix:

| Configuration | Record-path mutation detected? |
|---|---|
| HEAD as shipped, class alone | YES — 5/5 |
| HEAD as shipped, full suite | YES — 4/4 |
| A concurrent test appended to the class, class alone | **NO — 5/5 green** |
| A concurrent test appended to the class, full suite | **NO — 3/3 green under a mutation that must fail** |

Two controls locate the cause: a same-named stub occupying the same slot leaves detection intact
(so it is not merely being preceded), and shortening the successor's grace window from 2s to 50ms
loses detection (so it is not elapsed time either) — but the 50ms version with its tail assertions
stripped detects again. The margin is a few milliseconds of the predecessor's synchronous tail work.

This violates the plan's own global constraint that every race test be watched failing before it
counts. Human precedent (twice on this branch): close it, do not document-and-accept.

**Files:**
- Modify: `Modules/Inventory/Accounting101.Inventory.Tests/MovementFoldRaceTests.cs`
- Modify: `Modules/Inventory/Accounting101.Inventory.Tests/MovementVoidLockHoldTests.cs` (minor 3 below)
- Modify: `Modules/Inventory/Accounting101.Inventory.Tests/VoidSectionGateStore.cs` (minor 1 below)
- **No production file changes.** The production code is correct and stays byte-identical. If you
  conclude otherwise, STOP and report — that is a finding, not a licence to edit.

### The requirement

`Two_concurrent_issues_on_one_item_cannot_both_pass_block_negative` must detect its mutation because of
how it is BUILT, not because of where it lands in the schedule. The fix both the implementer and the
reviewer independently named: a second rendezvous after the fold's last read, mirroring the `afterFold`
rendezvous that `Two_issue_racers_on_one_retry_key_yield_one_create_and_one_replay` already uses —
`FoldCompleteRendezvousLedger` exists for exactly this and is already wired into that sibling.

### The acceptance bar — this is the whole point of the task

The old detection evidence is void; you are re-establishing it. Demonstrate detection in **all four**
cells of the matrix above, not just the two that already passed:

1. mutation + this class alone,
2. mutation + full suite,
3. mutation + a concurrent test appended to this class (the configuration that disarms it today — you
   must reproduce the disarming on HEAD first, so you know your reproduction works, and only then show
   your fix surviving it),
4. mutation + full suite with that concurrent test still appended.

Run each several times, not once — a flaky detector is what you are removing. Report the actual counts.
Then remove the temporary appended test, revert the mutation, and confirm a clean production tree with
`git diff --exit-code`.

### Also fix, in the same task (three Minors from Task 3b's review)

- `VoidSectionGateStore.cs:19` — says the gated call sits "four statements after" the acquire. It is
  the third; only two intervene. Fix the count or drop it.
- `MovementFoldRaceTests.cs:32` — says "~30 classes running in parallel". The project has 24 test
  classes (27 public, 3 of them fixtures).
- `MovementVoidLockHoldTests.cs:90-93` — `gate.Open()` is unreachable when the first assertion throws,
  so a failing run leaves the void parked until its 10s timeout fires into a task nobody awaits. Put
  the open in a `finally`.

### The trap this task is most likely to fall into

If your fix makes the flagship test order-independent, then the `DO NOT ADD A CONCURRENT TEST TO THIS
CLASS` warning block and the rationale for `MovementVoidLockHoldTests` living in its own class may both
become false — they document a fragility you just removed. Leaving them is defect class (4), which this
plan has now paid for **three** times, twice in this exact comment block. Re-read every claim in both
files' remarks after your change and make them true. Do NOT move the hold test back into
`MovementFoldRaceTests` unless you have proven detection survives it (cell 3 above); if you do move it,
cell 3 stops being hypothetical and becomes the shipped configuration.

- [ ] **Step 1: Reproduce the disarming on HEAD** (append a concurrent test, apply the record-path
  mutation, watch the class go green) — you cannot prove a fix against a repro you have not seen
- [ ] **Step 2: Apply the durable fix**
- [ ] **Step 3: Prove all four matrix cells, several runs each**
- [ ] **Step 4: Fix the three Minors**
- [ ] **Step 5: Reconcile every claim in both files' remarks** (see the trap above)
- [ ] **Step 6: Full module suite** — 119 currently green
- [ ] **Step 7: Revert all mutations and temporary tests; `git diff --exit-code` on production; commit**
  with an explicit `git add` file list (never `-A`; `UI/Angular/src/app/core/api/environment.ts` stays
  uncommitted)

---

## Task 4: Retire the stale narrowing comments

**Files:**
- Modify: `Modules/Inventory/Accounting101.Inventory/InventoryMovementService.cs` (the `SCOPE, deliberately partial` block inside `RecordAsync`)
- Modify: `Modules/Inventory/Accounting101.Inventory.Tests/MovementRaceTests.cs` (class remarks)
- Modify: `docs/superpowers/specs/2026-07-26-toctou-status-code-followups.md`

**Interfaces:**
- Consumes: the completed work of Tasks 2 and 3.
- Produces: nothing.

### Background the implementer needs

Three places now assert something untrue — that the fold race is unfixed. Left alone they will mislead the next reader into re-deriving a problem that no longer exists, which is exactly how the duplicate-handling arc's stale ledger caused rediscovery work.

- [ ] **Step 1: Correct the service's scope comment**

In `RecordAsync`, replace the comment block beginning `// SCOPE, deliberately partial.` and ending `// and is the subject of its own spec.` with:

```csharp
            // The pre-check above is inherently TOCTOU under concurrency, and the store's atomic create is
            // the arbiter for THIS response's status. The wider fold race it used to leave open — a loser
            // computing its cost from a fold the winner had already moved — is closed by the per-item lock
            // taken at the top of this method, which also means a same-key loser now reaches the replay
            // branch rather than racing here at all.
```

- [ ] **Step 2: Correct the old race test's remarks**

In `MovementRaceTests`, replace the paragraph beginning `/// DELIBERATELY A RECEIPT.` through `/// the wrong thing.` with:

```csharp
    /// DELIBERATELY A RECEIPT, and still worth keeping as a Receipt: this test pins the STORE's atomic
    /// create as the arbiter of created-vs-replayed, independently of the per-item lock added later. The
    /// fold race that once made an Issue unsuitable here is closed (see MovementFoldRaceTests), which now
    /// covers the Issue path directly.
```

- [ ] **Step 3: Correct the follow-ups doc**

In `docs/superpowers/specs/2026-07-26-toctou-status-code-followups.md`, under `## Still out of scope, per the spec`, replace the bullet beginning `- **Inventory's weighted-average fold race.**` through `its own spec is correspondingly smaller.**` with:

```markdown
- ~~**Inventory's weighted-average fold race.**~~ **CLOSED 2026-07-27** —
  [2026-07-27-inventory-fold-race-design.md](2026-07-27-inventory-fold-race-design.md). Investigation found
  a second, unrecorded and more serious face: two concurrent movements on the same item with DIFFERENT ids
  both passed the block-negative guard, driving on-hand negative permanently. Both faces are closed by a
  per-item write lock; single-instance only, by ratified decision.
```

- [ ] **Step 4: Verify nothing else still claims the race is open**

Run: `grep -rn "fold race" --include=*.cs --include=*.md . | grep -v "/obj/" | grep -v "/bin/"`
Expected: every remaining hit is either the new spec, the new plan, the new tests, or one of the three corrections above. Any other hit still asserting the race is open must be corrected the same way.

- [ ] **Step 5: Run the module suite**

Run: `dotnet test Modules/Inventory/Accounting101.Inventory.Tests`
Expected: PASS — comment-only changes.

- [ ] **Step 6: Commit**

```bash
git add Modules/Inventory/Accounting101.Inventory/InventoryMovementService.cs \
        Modules/Inventory/Accounting101.Inventory.Tests/MovementRaceTests.cs \
        docs/superpowers/specs/2026-07-26-toctou-status-code-followups.md
git commit -m "docs(inventory): retire the comments saying the fold race is open"
```

---

## Verification before completion

- [ ] `dotnet test Modules/Inventory/Accounting101.Inventory.Tests` — passes
- [ ] Full local solution run — no regressions against the 1613-test baseline
- [ ] The mutation in Task 2 Step 8 was actually run, and the two race tests were watched failing
- [ ] `git status` shows `UI/Angular/src/app/core/api/environment.ts` still modified and **uncommitted**
