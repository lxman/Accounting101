namespace Accounting101.Invoicing;

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
}

/// <summary>Resolves the chart accounts the payment recipes post to for a given client.</summary>
public interface IPaymentAccountsProvider
{
    Task<PaymentPostingAccounts> GetAsync(Guid clientId, CancellationToken ct = default);
}
