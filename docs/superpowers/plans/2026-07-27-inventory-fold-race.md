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

    /// <summary>FACE 1, on an ISSUE — the type MovementRaceTests deliberately avoids. Two racers share a
    /// retry key. Unfixed, the loser computes its cost from a fold that already contains the winner's
    /// movement and can throw for a movement that landed. Fixed, its pre-check runs after the winner
    /// finished, so it takes the replay branch and freezes the cost from the stored document.</summary>
    [Fact]
    public async Task Two_issue_racers_on_one_retry_key_yield_one_create_and_one_replay()
    {
        RaceRendezvous rendezvous = new(participants: 2, timeout: TimeSpan.FromSeconds(5));
        Rig rig = Build(rendezvous);
        Guid clientId = Guid.NewGuid();
        Item item = await SeedTenOnHandAsync(rig, clientId);

        Guid key = Guid.NewGuid();
        RecordMovement request = new(
            item.Id, MovementType.Issue, 6m, null, new DateOnly(2026, 1, 16), "issue", key);

        (StockMovement Movement, bool Replayed)[] results =
            await Task.WhenAll(rig.Racing.RecordAsync(clientId, request), rig.Racing.RecordAsync(clientId, request));

        Assert.Single(results, r => !r.Replayed);
        Assert.Single(results, r => r.Replayed);
        Assert.All(results, r => Assert.Equal(key, r.Movement.Id));
        Assert.Equal(results[0].Movement.ExtendedCost, results[1].Movement.ExtendedCost);

        ItemValuation after = await rig.Valuation.GetAsync(clientId, item.Id, includePending: true);
        Assert.Equal(4m, after.OnHand);          // the movement applied exactly once
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

        await Task.WhenAll(a, b);                // a TimeoutException here means the lock is too coarse
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

In `Modules/Inventory/Accounting101.Inventory.Tests/MovementRaceTests.cs`, change line 32 to:

```csharp
        InventoryMovementService service = new(items, racingStore, accounts, ledger, valuation, new ItemWriteLock());
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
Expected: `Two_concurrent_issues_on_one_item_cannot_both_pass_block_negative` FAILS (both succeed; on-hand −2) and `Two_issue_racers_on_one_retry_key_yield_one_create_and_one_replay` FAILS. `Writes_to_different_items_are_not_serialised` still passes — it is a liveness test and the mutation does not affect it.

Restore the `using` form and re-run: all 3 pass. Do not commit the mutation. Report the failing output.

- [ ] **Step 9: Commit**

```bash
git add Modules/Inventory/Accounting101.Inventory/InventoryMovementService.cs \
        Modules/Inventory/Accounting101.Inventory.Api/InventoryServiceExtensions.cs \
        Modules/Inventory/Accounting101.Inventory.Tests/RendezvousItemWriteLock.cs \
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
