using Accounting101.TestSupport;

namespace Accounting101.Banking.Reconciliation.Tests;

/// <summary>
/// The TOCTOU race on a retry key, pinned deterministically.
///
/// WHAT MAKES THIS VALID: delete the service's <c>replayed = !result.Created;</c> line (leaving
/// <c>replayed</c> at its earlier, pre-check-derived value) and both racers claim a create, so the
/// single-create assertion sees two and fails.
/// </summary>
public sealed class AdjustmentRaceTests
{
    private static readonly Guid Cash = Guid.NewGuid();
    private static readonly Guid Offset = Guid.NewGuid();
    private static readonly DateOnly StmtDate = new(2026, 1, 31);

    [Fact]
    public async Task Two_adjustment_racers_past_the_probe_yield_exactly_one_create()
    {
        InMemoryBankAdjustmentStore inner = new();
        RaceRendezvous rendezvous = new(participants: 2);
        RendezvousBankAdjustmentStore racingStore = new(inner, rendezvous);
        InMemoryReconciliationStore recs = new();
        FakePostingLedger ledger = new();
        AdjustmentService service = new(recs, racingStore, ledger);

        Guid clientId = Guid.NewGuid();
        Reconciliation rec = await recs.CreateAsync(clientId, Cash, Guid.NewGuid(), StmtDate);
        Guid retryKey = Guid.NewGuid();
        RecordAdjustmentInput body = new(Offset, 5m, AdjustmentKind.Charge, null, "bank fee");

        Task<(BankAdjustment Adjustment, bool Replayed)> first =
            service.RecordAdjustmentAsync(clientId, rec.Id, body, retryKey);
        Task<(BankAdjustment Adjustment, bool Replayed)> second =
            service.RecordAdjustmentAsync(clientId, rec.Id, body, retryKey);
        (BankAdjustment Adjustment, bool Replayed)[] results = await Task.WhenAll(first, second);

        Assert.Single(results, r => !r.Replayed);
        Assert.Single(results, r => r.Replayed);
        Assert.Equal(retryKey, results[0].Adjustment.Id);
    }
}
