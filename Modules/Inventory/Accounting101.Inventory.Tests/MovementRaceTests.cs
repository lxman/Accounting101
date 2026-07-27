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
/// DELIBERATELY A RECEIPT. Receipt and overage Adjustment compute extendedCost as
/// Round(qty x unitCost) — pure in the request — so the conversion fixes them completely. Issue and
/// shrinkage Adjustment derive cost from a pending-inclusive weighted-average fold the winner has
/// already moved, and that race is NOT fixed here (the cost is computed before the store call, so the
/// Created bool cannot help). A race test built on an Issue would fail for the fold reason and pin
/// the wrong thing.
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
