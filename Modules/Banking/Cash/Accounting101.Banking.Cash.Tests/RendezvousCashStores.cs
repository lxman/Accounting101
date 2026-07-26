using Accounting101.Ledger.Contracts;
using Accounting101.TestSupport;

namespace Accounting101.Banking.Cash.Tests;

/// <summary>
/// Blocks in the PROBE until every racer has arrived, forcing both callers to observe "not found"
/// before either reaches the atomic create — the interleaving CashService's pre-check is vulnerable
/// to, and the one real HTTP posts cannot be relied on to produce. Everything else delegates, so the
/// create is really arbitrated by the inner fake's TryAdd.
/// </summary>
internal sealed class RendezvousCashDisbursementStore(
    InMemoryCashDisbursementStore inner, RaceRendezvous rendezvous) : ICashDisbursementStore
{
    public async Task<CashDisbursement?> GetAsync(Guid clientId, Guid id, CancellationToken ct = default)
    {
        CashDisbursement? found = await inner.GetAsync(clientId, id, ct);
        await rendezvous.ArriveAndWaitAsync(ct);
        return found;
    }

    public Task<(CashDisbursement Disbursement, bool Created)> RecordAsync(
        Guid clientId, Guid id, CashDisbursementBody body, CancellationToken ct = default) =>
        inner.RecordAsync(clientId, id, body, ct);

    public Task VoidAsync(Guid clientId, Guid id, CancellationToken ct = default) =>
        inner.VoidAsync(clientId, id, ct);

    public Task<IReadOnlyList<CashDisbursement>> GetByClientAsync(Guid clientId, CancellationToken ct = default) =>
        inner.GetByClientAsync(clientId, ct);

    public Task<PagedResponse<CashDisbursement>> GetByClientPagedAsync(
        Guid clientId, int skip, int limit, bool descending, bool includeVoided, CancellationToken ct = default) =>
        inner.GetByClientPagedAsync(clientId, skip, limit, descending, includeVoided, ct);
}

internal sealed class RendezvousCashDepositStore(
    InMemoryCashDepositStore inner, RaceRendezvous rendezvous) : ICashDepositStore
{
    public async Task<CashDeposit?> GetAsync(Guid clientId, Guid id, CancellationToken ct = default)
    {
        CashDeposit? found = await inner.GetAsync(clientId, id, ct);
        await rendezvous.ArriveAndWaitAsync(ct);
        return found;
    }

    public Task<(CashDeposit Deposit, bool Created)> RecordAsync(
        Guid clientId, Guid id, CashDepositBody body, CancellationToken ct = default) =>
        inner.RecordAsync(clientId, id, body, ct);

    public Task VoidAsync(Guid clientId, Guid id, CancellationToken ct = default) =>
        inner.VoidAsync(clientId, id, ct);

    public Task<IReadOnlyList<CashDeposit>> GetByClientAsync(Guid clientId, CancellationToken ct = default) =>
        inner.GetByClientAsync(clientId, ct);

    public Task<PagedResponse<CashDeposit>> GetByClientPagedAsync(
        Guid clientId, int skip, int limit, bool descending, bool includeVoided, CancellationToken ct = default) =>
        inner.GetByClientPagedAsync(clientId, skip, limit, descending, includeVoided, ct);
}
