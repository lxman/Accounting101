using Accounting101.TestSupport;

namespace Accounting101.Receivables.Tests;

/// <summary>
/// Blocks in the PROBE methods until every racer has arrived, forcing both callers to observe
/// "not found" before either reaches the atomic create — the interleaving PaymentService's pre-check
/// is vulnerable to. Every other member delegates, so the create is really arbitrated by the inner
/// fake's TryAdd. Each test constructs its own <see cref="RaceRendezvous"/>, so the blocking probes
/// never interfere with one another.
/// </summary>
internal sealed class RendezvousPaymentStore(InMemoryPaymentStore inner, RaceRendezvous rendezvous)
    : IPaymentStore
{
    public async Task<Payment?> GetPaymentAsync(Guid clientId, Guid paymentId, CancellationToken ct = default)
    {
        Payment? found = await inner.GetPaymentAsync(clientId, paymentId, ct);
        await rendezvous.ArriveAndWaitAsync(ct);
        return found;
    }

    public async Task<CreditApplication?> GetCreditApplicationAsync(Guid clientId, Guid creditApplicationId, CancellationToken ct = default)
    {
        CreditApplication? found = await inner.GetCreditApplicationAsync(clientId, creditApplicationId, ct);
        await rendezvous.ArriveAndWaitAsync(ct);
        return found;
    }

    public Task<(Payment Payment, bool Created)> RecordPaymentAsync(
        Guid clientId, Guid id, PaymentBody body, CancellationToken ct = default) =>
        inner.RecordPaymentAsync(clientId, id, body, ct);

    public Task<(CreditApplication Application, bool Created)> RecordCreditApplicationAsync(
        Guid clientId, Guid id, CreditApplicationBody body, CancellationToken ct = default) =>
        inner.RecordCreditApplicationAsync(clientId, id, body, ct);

    // Every remaining IPaymentStore member delegates straight through, unblocked, for now.
    // Task 7 converts the write-off / credit-note / refund records and adds their blocking probes.
    public Task VoidAsync(Guid clientId, Guid documentId, CancellationToken ct = default) =>
        inner.VoidAsync(clientId, documentId, ct);

    public Task<IReadOnlyList<Payment>> GetPaymentsByCustomerAsync(Guid clientId, Guid customerId, CancellationToken ct = default) =>
        inner.GetPaymentsByCustomerAsync(clientId, customerId, ct);

    public Task<IReadOnlyList<CreditApplication>> GetCreditApplicationsByCustomerAsync(Guid clientId, Guid customerId, CancellationToken ct = default) =>
        inner.GetCreditApplicationsByCustomerAsync(clientId, customerId, ct);

    public Task<WriteOff> RecordWriteOffAsync(Guid clientId, Guid id, WriteOffBody body, CancellationToken ct = default) =>
        inner.RecordWriteOffAsync(clientId, id, body, ct);

    public Task<WriteOff?> GetWriteOffAsync(Guid clientId, Guid writeOffId, CancellationToken ct = default) =>
        inner.GetWriteOffAsync(clientId, writeOffId, ct);

    public Task<IReadOnlyList<WriteOff>> GetWriteOffsByCustomerAsync(Guid clientId, Guid customerId, CancellationToken ct = default) =>
        inner.GetWriteOffsByCustomerAsync(clientId, customerId, ct);

    public Task VoidWriteOffAsync(Guid clientId, Guid writeOffId, CancellationToken ct = default) =>
        inner.VoidWriteOffAsync(clientId, writeOffId, ct);

    public Task<CreditNote> RecordCreditNoteAsync(Guid clientId, Guid id, CreditNoteBody body, CancellationToken ct = default) =>
        inner.RecordCreditNoteAsync(clientId, id, body, ct);

    public Task<CreditNote?> GetCreditNoteAsync(Guid clientId, Guid creditNoteId, CancellationToken ct = default) =>
        inner.GetCreditNoteAsync(clientId, creditNoteId, ct);

    public Task<IReadOnlyList<CreditNote>> GetCreditNotesByCustomerAsync(Guid clientId, Guid customerId, CancellationToken ct = default) =>
        inner.GetCreditNotesByCustomerAsync(clientId, customerId, ct);

    public Task VoidCreditNoteAsync(Guid clientId, Guid creditNoteId, CancellationToken ct = default) =>
        inner.VoidCreditNoteAsync(clientId, creditNoteId, ct);

    public Task<Refund> RecordRefundAsync(Guid clientId, Guid id, RefundBody body, CancellationToken ct = default) =>
        inner.RecordRefundAsync(clientId, id, body, ct);

    public Task<Refund?> GetRefundAsync(Guid clientId, Guid refundId, CancellationToken ct = default) =>
        inner.GetRefundAsync(clientId, refundId, ct);

    public Task<IReadOnlyList<Refund>> GetRefundsByCustomerAsync(Guid clientId, Guid customerId, CancellationToken ct = default) =>
        inner.GetRefundsByCustomerAsync(clientId, customerId, ct);

    public Task VoidRefundAsync(Guid clientId, Guid refundId, CancellationToken ct = default) =>
        inner.VoidRefundAsync(clientId, refundId, ct);
}
