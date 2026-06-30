namespace Accounting101.Payables;

/// <summary>The module's bill store. Drafts live in a plain collection (editable, discardable scratch);
/// entered bills live in an evidentiary collection (append-only). Enter promotes a draft to a NEW evidentiary
/// document with a new id and deletes the draft — mirrors the invoicing module's two-tier split.</summary>
public interface IBillStore
{
    Task<Bill> CreateDraftAsync(Guid clientId, BillBody body, CancellationToken ct = default);
    Task<Bill> UpdateDraftAsync(Guid clientId, Guid billId, BillBody body, CancellationToken ct = default);
    Task DiscardDraftAsync(Guid clientId, Guid billId, CancellationToken ct = default);
    Task<Bill> PromoteDraftAsync(Guid clientId, Guid billId, CancellationToken ct = default);
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
