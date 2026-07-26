using Accounting101.Ledger.Contracts;
using Accounting101.TestSupport;

namespace Accounting101.Payroll.Tests;

/// <summary>
/// Blocks in the PROBE until every racer has arrived, forcing both callers to observe "not found"
/// before either reaches the atomic create — the interleaving PayrollService's pre-check is vulnerable
/// to. Everything else delegates, so the create is really arbitrated by the inner fake's TryAdd.
/// </summary>
internal sealed class RendezvousPayrollRunStore(
    InMemoryPayrollRunStore inner, RaceRendezvous rendezvous) : IPayrollRunStore
{
    public async Task<PayrollRun?> GetAsync(Guid clientId, Guid runId, CancellationToken ct = default)
    {
        PayrollRun? found = await inner.GetAsync(clientId, runId, ct);
        await rendezvous.ArriveAndWaitAsync(ct);
        return found;
    }

    public Task<(PayrollRun Run, bool Created)> RecordAsync(
        Guid clientId, Guid id, PayrollRunBody body, CancellationToken ct = default) =>
        inner.RecordAsync(clientId, id, body, ct);

    public Task VoidAsync(Guid clientId, Guid runId, CancellationToken ct = default) =>
        inner.VoidAsync(clientId, runId, ct);

    public Task<IReadOnlyList<PayrollRun>> GetByClientAsync(Guid clientId, CancellationToken ct = default) =>
        inner.GetByClientAsync(clientId, ct);

    public Task<PagedResponse<PayrollRun>> GetByClientPagedAsync(
        Guid clientId, int skip, int limit, bool descending, bool includeVoided, CancellationToken ct = default) =>
        inner.GetByClientPagedAsync(clientId, skip, limit, descending, includeVoided, ct);
}

internal sealed class RendezvousTaxRemittanceStore(
    InMemoryTaxRemittanceStore inner, RaceRendezvous rendezvous) : ITaxRemittanceStore
{
    public async Task<TaxRemittance?> GetAsync(Guid clientId, Guid remittanceId, CancellationToken ct = default)
    {
        TaxRemittance? found = await inner.GetAsync(clientId, remittanceId, ct);
        await rendezvous.ArriveAndWaitAsync(ct);
        return found;
    }

    public Task<(TaxRemittance Remittance, bool Created)> RecordAsync(
        Guid clientId, Guid id, TaxRemittanceBody body, CancellationToken ct = default) =>
        inner.RecordAsync(clientId, id, body, ct);

    public Task VoidAsync(Guid clientId, Guid remittanceId, CancellationToken ct = default) =>
        inner.VoidAsync(clientId, remittanceId, ct);

    public Task<IReadOnlyList<TaxRemittance>> GetByClientAsync(Guid clientId, CancellationToken ct = default) =>
        inner.GetByClientAsync(clientId, ct);

    public Task<PagedResponse<TaxRemittance>> GetByClientPagedAsync(
        Guid clientId, int skip, int limit, bool descending, bool includeVoided, CancellationToken ct = default) =>
        inner.GetByClientPagedAsync(clientId, skip, limit, descending, includeVoided, ct);
}
