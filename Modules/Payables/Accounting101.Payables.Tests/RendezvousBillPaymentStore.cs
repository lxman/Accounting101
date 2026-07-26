using Accounting101.TestSupport;

namespace Accounting101.Payables.Tests;

/// <summary>
/// Wraps <see cref="InMemoryBillPaymentStore"/> and blocks in the PROBE (<see cref="GetPaymentAsync"/>)
/// until every racer has arrived. That forces both callers to observe "not found" before either reaches
/// the atomic create — the exact interleaving <c>BillPaymentService</c>'s pre-check is vulnerable to, and
/// the one a pair of real HTTP posts cannot be relied on to produce.
///
/// Every other member delegates untouched, so the create really is arbitrated by the inner fake's
/// TryAdd — the honest stand-in for the engine's insert-only CreateWithIdAsync.
/// </summary>
internal sealed class RendezvousBillPaymentStore(InMemoryBillPaymentStore inner, RaceRendezvous rendezvous)
    : IBillPaymentStore
{
    public async Task<BillPayment?> GetPaymentAsync(Guid clientId, Guid paymentId, CancellationToken ct = default)
    {
        BillPayment? found = await inner.GetPaymentAsync(clientId, paymentId, ct);
        await rendezvous.ArriveAndWaitAsync(ct);
        return found;
    }

    public Task<(BillPayment Payment, bool Created)> RecordPaymentAsync(
        Guid clientId, Guid id, BillPaymentBody body, CancellationToken ct = default) =>
        inner.RecordPaymentAsync(clientId, id, body, ct);

    public Task<VendorCreditApplication> RecordCreditApplicationAsync(
        Guid clientId, Guid id, VendorCreditApplicationBody body, CancellationToken ct = default) =>
        inner.RecordCreditApplicationAsync(clientId, id, body, ct);

    public Task VoidAsync(Guid clientId, Guid paymentId, CancellationToken ct = default) =>
        inner.VoidAsync(clientId, paymentId, ct);

    public Task<IReadOnlyList<BillPayment>> GetPaymentsByVendorAsync(
        Guid clientId, Guid vendorId, CancellationToken ct = default) =>
        inner.GetPaymentsByVendorAsync(clientId, vendorId, ct);

    public Task<IReadOnlyList<VendorCreditApplication>> GetCreditApplicationsByVendorAsync(
        Guid clientId, Guid vendorId, CancellationToken ct = default) =>
        inner.GetCreditApplicationsByVendorAsync(clientId, vendorId, ct);

    public Task<VendorCreditApplication?> GetCreditApplicationAsync(
        Guid clientId, Guid creditApplicationId, CancellationToken ct = default) =>
        inner.GetCreditApplicationAsync(clientId, creditApplicationId, ct);
}
