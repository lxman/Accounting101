namespace Accounting101.Payables;

/// <summary>The module's bill store — evidentiary documents backed by the engine's document store.
/// Draft/finalize/void lifecycle mirrors the invoicing module with vendor-scoped tags.</summary>
public interface IBillStore
{
    Task<Bill> CreateDraftAsync(Guid clientId, BillBody body, CancellationToken ct = default);
    Task<Bill> FinalizeAsync(Guid clientId, Guid billId, CancellationToken ct = default);
    Task VoidAsync(Guid clientId, Guid billId, CancellationToken ct = default);
    Task<Bill?> GetAsync(Guid clientId, Guid billId, CancellationToken ct = default);
    Task<IReadOnlyList<Bill>> GetByVendorAsync(Guid clientId, Guid vendorId, CancellationToken ct = default);
}

/// <summary>The module's bill payment store — evidentiary payment and credit-application documents backed
/// by the engine's document store. Voided is derived from the document lifecycle.</summary>
public interface IBillPaymentStore
{
    Task<BillPayment> RecordPaymentAsync(Guid clientId, BillPaymentBody body, CancellationToken ct = default);
    Task<VendorCreditApplication> RecordCreditApplicationAsync(Guid clientId, VendorCreditApplicationBody body, CancellationToken ct = default);
    Task VoidAsync(Guid clientId, Guid paymentId, CancellationToken ct = default);
    Task<BillPayment?> GetPaymentAsync(Guid clientId, Guid paymentId, CancellationToken ct = default);
    Task<IReadOnlyList<BillPayment>> GetPaymentsByVendorAsync(Guid clientId, Guid vendorId, CancellationToken ct = default);
    Task<IReadOnlyList<VendorCreditApplication>> GetCreditApplicationsByVendorAsync(Guid clientId, Guid vendorId, CancellationToken ct = default);
}

/// <summary>Resolves the chart accounts the bill and payment recipes post to for a given client.</summary>
public interface IBillAccountsProvider
{
    Task<BillPostingAccounts> GetBillAccountsAsync(Guid clientId, CancellationToken ct = default);
    Task<BillPaymentPostingAccounts> GetPaymentAccountsAsync(Guid clientId, CancellationToken ct = default);
}
