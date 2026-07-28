using Accounting101.Ledger.Contracts;
using Accounting101.TestSupport;

namespace Accounting101.Inventory.Tests;

/// <summary>
/// The weighted-average fold race, both faces, pinned deterministically.
///
/// WHAT MAKES <see cref="Two_concurrent_issues_on_one_item_cannot_both_pass_block_negative"/> VALID:
/// change the RECORD path's acquire to release immediately —
/// <c>(await itemLock.AcquireAsync(clientId, request.ItemId, ct)).Dispose();</c> instead of holding it
/// with <c>using</c> — and THAT test fails (both issues succeed, on-hand lands at -2). Removing the lock
/// outright is NOT a valid mutation: it removes the rendezvous with it, so the racers no longer overlap
/// and the test proves nothing. The other three tests below are unaffected by this mutation — one builds
/// with <see cref="NoOpItemWriteLock"/>, so it runs the mutated line against a lock whose holder does
/// nothing whether it is disposed early or late; another is a liveness check; and the third
/// (<see cref="Recording_and_voiding_both_acquire_the_lock_for_the_movements_item"/>)
/// is a SEQUENTIAL spy test that asserts only which keys were acquired, so it is blind to EITHER
/// acquire-but-don't-hold mutation — disposing immediately still enqueues the same two keys — and each
/// names its own mutation in its own remarks.
///
/// THAT DETECTION IS STRUCTURAL, AND IT WAS NOT ALWAYS. Until 2026-07-27 the test held its racers with a
/// single rendezvous taken BEFORE the lock acquire and then left the thread pool to interleave the two
/// folds. It usually did — but a concurrent predecessor in the same class was enough to stop it, and
/// xUnit orders a class's tests by a hash of their names, not by source position, so an appended test can
/// land ahead of this one whatever it is called. Measured with a copy of
/// <see cref="MovementVoidLockHoldTests.A_record_cannot_proceed_while_a_void_holds_the_items_lock"/>
/// appended to this class: under the mutation above the class went 5 runs out of 5 GREEN, and the full
/// module suite 2 out of 2 green, where every run had to fail.
/// <see cref="FoldOverlapGate"/> closes that hole by construction — a racer whose fold completes while it
/// does NOT hold the item lock waits there for the other racer's fold, so under the mutation both folds
/// are taken before either write, both read on-hand 10, and both clear block-negative in EVERY ordering.
/// Under the shipped code every racer holds the lock through its own fold, so the gate is never waited on
/// and cannot strand. Both configurations were then re-measured green-when-clean and red-when-mutated,
/// with and without that appended predecessor, class-alone and full-suite. So a concurrent test may now
/// be added to this class — but do not go back to a bare pre-acquire rendezvous, which is what made the
/// detection incidental in the first place. The void path's hold test stays in
/// <see cref="MovementVoidLockHoldTests"/> on its own merits, described there.
/// </summary>
public sealed class MovementFoldRaceTests
{
    private sealed record Rig(
        InventoryMovementService Seeder, InventoryMovementService RacerA, InventoryMovementService RacerB,
        InMemoryItemStore Items, ItemValuationService Valuation);

    /// <summary>Three services over ONE set of stores, one ledger fake and one real
    /// <see cref="ItemWriteLock"/>, so all of them are genuinely mutually exclusive.
    ///
    /// The seeder is undecorated on every seam, so setup calls can never arrive at either rendezvous and
    /// strand it. The two racers are decorated identically but SEPARATELY: each gets its own
    /// <see cref="FoldOverlapRacer"/>, which is at once its item write lock (wrapping the shared
    /// pre-acquire rendezvous over the shared real lock) and the fold-complete hook of its own ledger
    /// decorator. One racer per pair is what lets <see cref="FoldOverlapGate"/> answer "is THIS racer
    /// holding the lock?" without a window — see its remarks. Everything underneath is shared, so the
    /// domain race is exactly the one two HTTP callers would run.</summary>
    private static Rig Build(RaceRendezvous atAcquire, FoldOverlapGate foldGate)
    {
        InMemoryItemStore items = new();
        InMemoryStockMovementStore movements = new();
        FakeLedgerClient ledger = new();
        FixedInventoryAccountsProvider accounts = new();
        ItemValuationService seedValuation = new(movements, accounts, ledger);
        ItemWriteLock realLock = new();
        IItemWriteLock contended = new RendezvousItemWriteLock(realLock, atAcquire);

        return new Rig(
            new InventoryMovementService(items, movements, accounts, ledger, seedValuation, realLock),
            BuildRacer(), BuildRacer(), items, seedValuation);

        InventoryMovementService BuildRacer()
        {
            FoldOverlapRacer racer = foldGate.NewRacer(contended);
            ILedgerClient racerLedger = new FoldCompleteRendezvousLedger(ledger, racer.FoldCompletedAsync);
            ItemValuationService racerValuation = new(movements, accounts, racerLedger);
            return new InventoryMovementService(items, movements, accounts, racerLedger, racerValuation, racer);
        }
    }

    private static async Task<Item> SeedTenOnHandAsync(Rig rig, Guid clientId)
    {
        Item item = await rig.Items.CreateAsync(clientId, new ItemBody("SKU1", "Widget", null, "each"));
        await rig.Seeder.RecordAsync(clientId, new RecordMovement(
            item.Id, MovementType.Receipt, 10m, 2m, new DateOnly(2026, 1, 15), "seed"));
        return item;
    }

    /// <summary>FACE 2, the money bug. Both racers read (OnHand 10, Value 20.00 — the seed's 10 @ 2.00),
    /// both issue 6, and InventoryValuation.Issue's <c>quantity > current.OnHand</c> guard passes for BOTH
    /// because neither movement is in the other's fold. Unfixed, on-hand lands at -2 and stays there.
    ///
    /// TWO SEAMS, and both are needed. <c>atAcquire</c> (inside <see cref="RendezvousItemWriteLock"/>,
    /// BEFORE the acquire) puts both racers in flight at the lock at the same instant, so the shipped
    /// build's pass is a real contention outcome and not two calls that happened to run one after the
    /// other. <c>foldGate</c> (after the fold's LAST read) forces the two folds to OVERLAP in any build
    /// where the lock is not held across them — the mutation — and is inert in the build where it is.
    /// Neither is a timing window: see <see cref="FoldOverlapGate"/> and this class's remarks.</summary>
    [Fact]
    public async Task Two_concurrent_issues_on_one_item_cannot_both_pass_block_negative()
    {
        RaceRendezvous atAcquire = new(participants: 2, timeout: TimeSpan.FromSeconds(5));
        FoldOverlapGate foldGate = new(racers: 2, timeout: TimeSpan.FromSeconds(5));
        Rig rig = Build(atAcquire, foldGate);
        Guid clientId = Guid.NewGuid();
        Item item = await SeedTenOnHandAsync(rig, clientId);

        Task<(StockMovement Movement, bool Replayed)> a = rig.RacerA.RecordAsync(clientId, new RecordMovement(
            item.Id, MovementType.Issue, 6m, null, new DateOnly(2026, 1, 16), "issue A", Guid.NewGuid()));
        Task<(StockMovement Movement, bool Replayed)> b = rig.RacerB.RecordAsync(clientId, new RecordMovement(
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
        FoldCompleteRendezvousLedger racingLedger = new(ledger, afterFold.ArriveAndWaitAsync);   // blocks after the FOLD
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

    /// <summary>Both write paths must lock the item they are about to move. A void that did not lock could
    /// interleave with a record and let it compute from a fold mid-void.
    ///
    /// WHAT MAKES IT VALID: remove the <c>itemLock.AcquireAsync</c> call from <c>VoidAsync</c> and the
    /// second assertion sees 1 acquisition, not 2 — exactly the red-step failure observed before the
    /// production fix landed. This test is SEQUENTIAL, not concurrent — it asserts only which keys were
    /// acquired, not how long the lock is held, so it is blind to (and does not claim to cover) an
    /// acquire-but-don't-hold mutation: disposing immediately still enqueues the same two keys.</summary>
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
}
