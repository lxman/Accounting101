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
/// create as the arbiter of created-vs-replayed, independently of the per-item lock added later. The
/// fold race that once made an Issue unsuitable here is closed (see MovementFoldRaceTests), which now
/// covers the Issue path directly.
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
