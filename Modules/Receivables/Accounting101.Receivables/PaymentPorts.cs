namespace Accounting101.Receivables;

/// <summary>The module's payment store — evidentiary documents (payments + credit applications) backed by
/// the engine's document store. Voided is derived from the document lifecycle.</summary>
public interface IPaymentStore
{
    Task<Payment> RecordPaymentAsync(Guid clientId, PaymentBody body, CancellationToken ct = default);
    Task<CreditApplication> RecordCreditApplicationAsync(Guid clientId, CreditApplicationBody body, CancellationToken ct = default);
    Task VoidAsync(Guid clientId, Guid documentId, CancellationToken ct = default);
    Task<Payment?> GetPaymentAsync(Guid clientId, Guid paymentId, CancellationToken ct = default);
    Task<IReadOnlyList<Payment>> GetPaymentsByCustomerAsync(Guid clientId, Guid customerId, CancellationToken ct = default);
    Task<IReadOnlyList<CreditApplication>> GetCreditApplicationsByCustomerAsync(Guid clientId, Guid customerId, CancellationToken ct = default);

    Task<WriteOff> RecordWriteOffAsync(Guid clientId, WriteOffBody body, CancellationToken ct = default);
    Task<WriteOff?> GetWriteOffAsync(Guid clientId, Guid writeOffId, CancellationToken ct = default);
    Task<IReadOnlyList<WriteOff>> GetWriteOffsByCustomerAsync(Guid clientId, Guid customerId, CancellationToken ct = default);
    Task VoidWriteOffAsync(Guid clientId, Guid writeOffId, CancellationToken ct = default);

    Task<CreditNote> RecordCreditNoteAsync(Guid clientId, CreditNoteBody body, CancellationToken ct = default);
    Task<CreditNote?> GetCreditNoteAsync(Guid clientId, Guid creditNoteId, CancellationToken ct = default);
    Task<IReadOnlyList<CreditNote>> GetCreditNotesByCustomerAsync(Guid clientId, Guid customerId, CancellationToken ct = default);
    Task VoidCreditNoteAsync(Guid clientId, Guid creditNoteId, CancellationToken ct = default);

    Task<Refund> RecordRefundAsync(Guid clientId, RefundBody body, CancellationToken ct = default);
    Task<Refund?> GetRefundAsync(Guid clientId, Guid refundId, CancellationToken ct = default);
    Task<IReadOnlyList<Refund>> GetRefundsByCustomerAsync(Guid clientId, Guid customerId, CancellationToken ct = default);
    Task VoidRefundAsync(Guid clientId, Guid refundId, CancellationToken ct = default);
}

/// <summary>Resolves the chart accounts the payment recipes post to for a given client.</summary>
public interface IPaymentAccountsProvider
{
    Task<PaymentPostingAccounts> GetAsync(Guid clientId, CancellationToken ct = default);
}
