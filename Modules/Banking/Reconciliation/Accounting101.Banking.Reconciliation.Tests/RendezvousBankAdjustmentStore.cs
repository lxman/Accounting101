using Accounting101.TestSupport;

namespace Accounting101.Banking.Reconciliation.Tests;

/// <summary>
/// Blocks in the PROBE until every racer has arrived, forcing both callers to observe "not found"
/// before either reaches the atomic create — the interleaving AdjustmentService's pre-check is
/// vulnerable to. Everything else delegates, so the create is really arbitrated by the inner fake's
/// TryAdd.
/// </summary>
internal sealed class RendezvousBankAdjustmentStore(
    InMemoryBankAdjustmentStore inner, RaceRendezvous rendezvous) : IBankAdjustmentStore
{
    public async Task<BankAdjustment?> GetAsync(Guid clientId, Guid id, CancellationToken ct = default)
    {
        BankAdjustment? found = await inner.GetAsync(clientId, id, ct);
        await rendezvous.ArriveAndWaitAsync(ct);
        return found;
    }

    public Task<(BankAdjustment Adjustment, bool Created)> RecordAsync(
        Guid clientId, Guid id, BankAdjustmentBody body, CancellationToken ct = default) =>
        inner.RecordAsync(clientId, id, body, ct);

    public Task VoidAsync(Guid clientId, Guid id, CancellationToken ct = default) =>
        inner.VoidAsync(clientId, id, ct);

    public Task<IReadOnlyList<BankAdjustment>> GetByReconciliationAsync(
        Guid clientId, Guid reconciliationId, CancellationToken ct = default) =>
        inner.GetByReconciliationAsync(clientId, reconciliationId, ct);
}
