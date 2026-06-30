namespace Accounting101.Receivables;

/// <summary>The module's customer store — reference data in the client's database, via the engine's document store.</summary>
public interface ICustomerStore
{
    Task SaveAsync(Guid clientId, Customer customer, CancellationToken cancellationToken = default);
    Task<Customer?> GetAsync(Guid clientId, Guid customerId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Customer>> ListAsync(Guid clientId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The module's invoice store — two-tier storage: drafts in a plain "invoice-drafts" collection
/// (freely editable and discardable), and issued invoices in the evidentiary "invoices" collection
/// (append-only, numbered). Only PromoteDraftAsync crosses the tier boundary; only Issue/Void are
/// part of the evidentiary record.
/// </summary>
public interface IInvoiceStore
{
    /// <summary>Create a draft invoice in the plain collection (no number, no ledger effect). Returns it with its assigned id.</summary>
    Task<Invoice> CreateDraftAsync(Guid clientId, InvoiceBody body, CancellationToken cancellationToken = default);

    /// <summary>Replace a draft's body in the plain collection. Throws <see cref="InvalidOperationException"/> if the id is not a draft.</summary>
    Task<Invoice> UpdateDraftAsync(Guid clientId, Guid invoiceId, InvoiceBody body, CancellationToken cancellationToken = default);

    /// <summary>Delete a draft from the plain collection. Throws <see cref="InvalidOperationException"/> if the id is not a draft.</summary>
    Task DiscardDraftAsync(Guid clientId, Guid invoiceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Promote a draft: copy its body into the evidentiary "invoices" collection (assigning a gapless number),
    /// finalize the issued document, and delete the plain draft. Returns the issued invoice with a NEW id.
    /// </summary>
    Task<Invoice> PromoteDraftAsync(Guid clientId, Guid invoiceId, CancellationToken cancellationToken = default);

    /// <summary>Void an issued invoice (no successor).</summary>
    Task VoidAsync(Guid clientId, Guid invoiceId, CancellationToken cancellationToken = default);

    /// <summary>Reads from both tiers: checks the plain draft collection first, then the evidentiary collection.</summary>
    Task<Invoice?> GetAsync(Guid clientId, Guid invoiceId, CancellationToken cancellationToken = default);

    /// <summary>All of a customer's live invoices spanning both tiers (drafts + issued; voided are hidden).</summary>
    Task<IReadOnlyList<Invoice>> GetByCustomerAsync(Guid clientId, Guid customerId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves the chart accounts the receivables recipe posts to for a given client. The accounts differ per
/// client, so this is resolved per call, not configured once.
/// </summary>
public interface IInvoiceAccountsProvider
{
    Task<InvoicePostingAccounts> GetAsync(Guid clientId, CancellationToken cancellationToken = default);
}
