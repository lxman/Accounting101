using Accounting101.Ledger.Contracts;
using Accounting101.TestSupport;

namespace Accounting101.Inventory.Tests;

/// <summary>
/// The TOCTOU race on a retry key, pinned deterministically.
///
/// WHAT MAKES THIS VALID: delete the service's <c>replayed = !result.Created;</c> line (leaving
/// <c>replayed</c> at its earlier, pre-check-derived value) and both racers claim a create, so the
/// single-create assertion sees two and fails.
///
/// DELIBERATELY A RECEIPT, and still worth keeping as a Receipt: this test pins the STORE's atomic
/// create as the arbiter of created-vs-replayed, independently of the per-item lock — which is exactly
/// why it builds on <see cref="NoOpItemWriteLock"/> below.
///
/// THE FOLD RACE IS CLOSED IN THE SERVICE, NOT IN THIS RIG. Running unlocked is the whole point here,
/// so this is precisely the build where that race is still live, and an Issue would still fail for the
/// fold reason and pin the wrong thing. <see cref="MovementFoldRaceTests"/> covers the Issue path
/// directly, on a rig that needs THREE things this one does not have:
///   - Stock to issue. Here the item is created and the raced Receipt is its FIRST movement, so on-hand
///     is 0 and an Issue of any size block-negatives on both racers immediately. Seed it WITHOUT a retry
///     key: the probe the rendezvous lives in runs only when one is supplied, so an unkeyed seed passes
///     straight through this rig's decorated store, while a keyed one arrives at a 2-participant
///     rendezvous alone and dies on RaceRendezvous's 10s timeout. (MovementFoldRaceTests sidesteps the
///     question with a separate undecorated seeder service; this class has only the one decorated.)
///   - A quantity small enough that BOTH racers clear block-negative under every ordering — 3 against a
///     seed of 10. At 6 the loser sees the reduced on-hand, throws, and the test would be asserting
///     protected behaviour from unprotected code.
///   - A second rendezvous after the fold's LAST read. The fold is three reads, and unguarded a racer
///     resumes with a stale value and a fresh quantity, composing 8.57 against 6.00 and taking a
///     divergent-content rejection. This rig has one rendezvous, in the probe, and no fold rendezvous.
/// Read that test rather than converting this one.
/// </summary>
public sealed class MovementRaceTests
{
    [Fact]
    public async Task Two_receipt_racers_past_the_probe_yield_exactly_one_create()
    {
        InMemoryItemStore items = new();
        InMemoryStockMovementStore inner = new();
        RaceRendezvous rendezvous = new(participants: 2);
        RendezvousStockMovementStore racingStore = new(inner, rendezvous);
        FakeLedgerClient ledger = new();
        FixedInventoryAccountsProvider accounts = new();
        ItemValuationService valuation = new(racingStore, accounts, ledger);
        // NoOp on purpose: this test pins the STORE's insert-only create as the arbiter of
        // created-vs-replayed — the guarantee that holds even where an in-process lock would not. Its
        // rendezvous blocks inside the critical section, so the real lock would strand the second racer.
        InventoryMovementService service = new(items, racingStore, accounts, ledger, valuation, new NoOpItemWriteLock());

        Guid clientId = Guid.NewGuid();
        Item item = await items.CreateAsync(clientId, new ItemBody("SKU1", "Widget", null, "each"));

        Guid key = Guid.NewGuid();
        // A Receipt — see the class remarks. Positive quantity, positive unit cost.
        RecordMovement request = new(
            item.Id, MovementType.Receipt, 10m, 2m, new DateOnly(2026, 1, 15), "Initial receipt", key);

        Task<(StockMovement Movement, bool Replayed)> first = service.RecordAsync(clientId, request);
        Task<(StockMovement Movement, bool Replayed)> second = service.RecordAsync(clientId, request);
        (StockMovement Movement, bool Replayed)[] results = await Task.WhenAll(first, second);

        Assert.Single(results, r => !r.Replayed);
        Assert.Single(results, r => r.Replayed);
        Assert.Equal(key, results[0].Movement.Id);
    }
}
