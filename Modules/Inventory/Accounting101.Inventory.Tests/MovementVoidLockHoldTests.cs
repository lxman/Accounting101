namespace Accounting101.Inventory.Tests;

/// <summary>
/// The VOID path's hold property: <c>InventoryMovementService.VoidAsync</c> does not merely ACQUIRE the
/// item lock, it HOLDS it for its whole body. That is what actually serialises a void against a
/// concurrent record, and <see cref="MovementFoldRaceTests"/> covers none of it — its spy test asserts
/// only which keys were acquired and says so in its own remarks.
///
/// WHY THIS IS ITS OWN CLASS, not an append to <see cref="MovementFoldRaceTests"/> as the task brief
/// assumed. That class's <c>Two_concurrent_issues_on_one_item_cannot_both_pass_block_negative</c>
/// detects its own mutation only while it runs first in its class out of a cold thread pool, and xUnit
/// orders a class's tests by a name hash — appending this one landed it in front and silently disarmed
/// that test, 5/5 green under a mutation that should have failed it. See that class's remarks for the
/// full measurement, including the two controls that place the blame on the concurrent work rather than
/// on ordering alone. A predecessor in a DIFFERENT class does not disarm it: with this test living here,
/// the full suite catches the record-path mutation 4 runs out of 4.
/// </summary>
public sealed class MovementVoidLockHoldTests
{
    /// <summary>
    /// WHAT MAKES IT VALID: apply the acquire-but-don't-hold mutation to <c>VoidAsync</c> —
    /// <c>(await itemLock.AcquireAsync(clientId, forKey.ItemId, ct)).Dispose();</c> in place of the
    /// <c>using</c> declaration — and this test fails on its first assertion, deterministically and in
    /// ~60ms: the racing record runs straight through to completion while the void sits parked mid-body.
    /// This is the ONLY test that covers that mutation; the record path's twin is covered in
    /// <see cref="MovementFoldRaceTests"/>, and neither mutation is detected by the other's test.
    ///
    /// THE GATE IS ONE-SIDED, AND HAS TO BE. Only one party can ever be inside a section the lock
    /// protects, so a two-party <see cref="TestSupport.RaceRendezvous"/> placed in there would wait
    /// forever for an arrival that is parked on the semaphore, and die on its timeout —
    /// <see cref="RendezvousItemWriteLock"/>'s remarks describe exactly that trap.
    /// <see cref="VoidSectionGateStore"/> instead parks its caller until the TEST thread opens it, and
    /// the test thread is not competing for the lock.
    ///
    /// WHAT THE FIRST ASSERTION CAN STILL OBSERVE once the lock is in place. The racer is built over
    /// UNDECORATED stores and an undecorated ledger, and <c>GetLatestForItemAsync</c> — the only gated
    /// method — is called from nowhere in the module but <c>VoidAsync</c>'s LIFO guard. So the item lock
    /// is the single thing that can stop this racer, and "it has not completed" therefore means "it is
    /// parked on the item lock", not "it is stuck somewhere incidental".
    ///
    /// WHY THE SECOND ASSERTION IS NOT REDUNDANT. The gate blocks BEFORE delegating, so the void's LIFO
    /// read is taken only after the gate opens. Under the mutation the racer's movement would by then be
    /// the item's latest and the guard would throw "Only the most recent movement for this item can be
    /// voided" — verified by running the mutation with the first assertion removed. That is the concrete
    /// domain harm the hold prevents, observed independently of the racer's task state.
    ///
    /// NO DEADLOCK IS POSSIBLE. Nothing that needs the item lock ever blocks inside the section: the gate
    /// is opened by the test thread, which holds no lock and is not racing for one, and it is opened
    /// unconditionally before either task is awaited.
    /// </summary>
    [Fact]
    public async Task A_record_cannot_proceed_while_a_void_holds_the_items_lock()
    {
        InMemoryItemStore items = new();
        InMemoryStockMovementStore movements = new();
        FakeLedgerClient ledger = new();
        FixedInventoryAccountsProvider accounts = new();
        ItemValuationService valuation = new(movements, accounts, ledger);
        ItemWriteLock realLock = new();

        // Seeder AND racer, undecorated on every seam — it can never reach the gate, so the item lock is
        // the only thing that can stop it. Seeding through it also cannot strand for the same reason.
        InventoryMovementService writer = new(items, movements, accounts, ledger, valuation, realLock);

        // The voider: same stores, same lock, movement store gated. Nothing else differs.
        VoidSectionGateStore gate = new(movements, TimeSpan.FromSeconds(10));
        InventoryMovementService voider = new(items, gate, accounts, ledger, valuation, realLock);

        Guid clientId = Guid.NewGuid();
        Item item = await items.CreateAsync(clientId, new ItemBody("SKU1", "Widget", null, "each"));
        (StockMovement Movement, bool Replayed) seed = await writer.RecordAsync(clientId, new RecordMovement(
            item.Id, MovementType.Receipt, 10m, 2m, new DateOnly(2026, 1, 15), "seed"));

        // Every fake completes inline and the lock is uncontended, so the void runs all the way to the
        // gate synchronously: by the time this call hands back its task it is already parked, holding the
        // item lock. The await below is belt-and-braces, and bounded so a mis-wiring fails fast.
        Task<StockMovement> voiding = voider.VoidAsync(clientId, seed.Movement.Id, "undo");
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10));

        Task<(StockMovement Movement, bool Replayed)> recording = writer.RecordAsync(clientId, new RecordMovement(
            item.Id, MovementType.Receipt, 5m, 3m, new DateOnly(2026, 1, 16), "concurrent", Guid.NewGuid()));

        // The only timing in the test, and it is generous on purpose: the dangerous direction is a FALSE
        // GREEN, a window so short that a mutated build merely LOOKS blocked. A mutated build in fact
        // needs none of it — with the void's lock released, every fake completes inline and RecordAsync
        // runs to completion on this very thread, so its task is already finished when it is handed back
        // (measured: 5/5 mutated runs failed here in ~60ms, never touching the two seconds). The WhenAny
        // means only an UNmutated run pays them.
        await Task.WhenAny(recording, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.False(recording.IsCompleted,
            "A record on the same item ran to completion while a void was mid-body — the void is not HOLDING the item lock.");

        gate.Open();

        // The void's LIFO guard reads the item's latest movement only now. It still sees the seed, because
        // the racer never got to insert; had it, this would throw and take the whole test with it.
        StockMovement voided = await voiding;
        Assert.Equal(MovementStatus.Void, voided.Status);

        // ...and the racer is blocked, not starved: it lands the moment the void releases.
        (StockMovement Movement, bool Replayed) recorded = await recording;
        Assert.False(recorded.Replayed);
        ItemValuation after = await valuation.GetAsync(clientId, item.Id, includePending: true);
        Assert.Equal(5m, after.OnHand);          // the voided seed dropped out; only the racer's receipt stands
        Assert.Equal(15m, after.TotalValue);     // 5 @ 3.00, costed off a fold that saw the void land first
    }
}
