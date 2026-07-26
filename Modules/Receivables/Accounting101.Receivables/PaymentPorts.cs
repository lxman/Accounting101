namespace Accounting101.Receivables;

/// <summary>The module's payment store — evidentiary documents (payments + credit applications) backed by
/// the engine's document store. Voided is derived from the document lifecycle.</summary>
public interface IPaymentStore
{
    /// <summary>Records the payment under <paramref name="id"/>. <c>Created</c> is <c>true</c> only for the
    /// caller that won the underlying atomic create; under two concurrent callers racing the SAME id exactly
    /// one gets <c>true</c>, and the loser still gets back the winner's persisted document but must report
    /// its own call as a replay. A caller cannot infer this from an earlier existence check — that check is
    /// TOCTOU under concurrency.</summary>
    Task<(Payment Payment, bool Created)> RecordPaymentAsync(Guid clientId, Guid id, PaymentBody body, CancellationToken ct = default);

    /// <summary>Records the credit application under <paramref name="id"/>. <c>Created</c> is <c>true</c>
    /// only for the caller that won the underlying atomic create — see the remarks on
    /// <see cref="RecordPaymentAsync"/>.</summary>
    Task<(CreditApplication Application, bool Created)> RecordCreditApplicationAsync(Guid clientId, Guid id, CreditApplicationBody body, CancellationToken ct = default);
    Task VoidAsync(Guid clientId, Guid documentId, CancellationToken ct = default);
    Task<Payment?> GetPaymentAsync(Guid clientId, Guid paymentId, CancellationToken ct = default);
    Task<IReadOnlyList<Payment>> GetPaymentsByCustomerAsync(Guid clientId, Guid customerId, CancellationToken ct = default);
    Task<IReadOnlyList<CreditApplication>> GetCreditApplicationsByCustomerAsync(Guid clientId, Guid customerId, CancellationToken ct = default);
    Task<CreditApplication?> GetCreditApplicationAsync(Guid clientId, Guid creditApplicationId, CancellationToken ct = default);

    /// <summary>Records the write-off under <paramref name="id"/>. <c>Created</c> is <c>true</c> only for
    /// the caller that won the underlying atomic create — see the remarks on <see cref="RecordPaymentAsync"/>.</summary>
    Task<(WriteOff WriteOff, bool Created)> RecordWriteOffAsync(Guid clientId, Guid id, WriteOffBody body, CancellationToken ct = default);
    Task<WriteOff?> GetWriteOffAsync(Guid clientId, Guid writeOffId, CancellationToken ct = default);
    Task<IReadOnlyList<WriteOff>> GetWriteOffsByCustomerAsync(Guid clientId, Guid customerId, CancellationToken ct = default);
    Task VoidWriteOffAsync(Guid clientId, Guid writeOffId, CancellationToken ct = default);

    /// <summary>Records the credit note under <paramref name="id"/>. <c>Created</c> is <c>true</c> only for
    /// the caller that won the underlying atomic create — see the remarks on <see cref="RecordPaymentAsync"/>.</summary>
    Task<(CreditNote CreditNote, bool Created)> RecordCreditNoteAsync(Guid clientId, Guid id, CreditNoteBody body, CancellationToken ct = default);
    Task<CreditNote?> GetCreditNoteAsync(Guid clientId, Guid creditNoteId, CancellationToken ct = default);
    Task<IReadOnlyList<CreditNote>> GetCreditNotesByCustomerAsync(Guid clientId, Guid customerId, CancellationToken ct = default);
    Task VoidCreditNoteAsync(Guid clientId, Guid creditNoteId, CancellationToken ct = default);

    /// <summary>Records the refund under <paramref name="id"/>. <c>Created</c> is <c>true</c> only for the
    /// caller that won the underlying atomic create — see the remarks on <see cref="RecordPaymentAsync"/>.</summary>
    Task<(Refund Refund, bool Created)> RecordRefundAsync(Guid clientId, Guid id, RefundBody body, CancellationToken ct = default);
    Task<Refund?> GetRefundAsync(Guid clientId, Guid refundId, CancellationToken ct = default);
    Task<IReadOnlyList<Refund>> GetRefundsByCustomerAsync(Guid clientId, Guid customerId, CancellationToken ct = default);
    Task VoidRefundAsync(Guid clientId, Guid refundId, CancellationToken ct = default);
}

/// <summary>Resolves the chart accounts the payment recipes post to for a given client.</summary>
public interface IPaymentAccountsProvider
{
    Task<PaymentPostingAccounts> GetAsync(Guid clientId, CancellationToken ct = default);
}
