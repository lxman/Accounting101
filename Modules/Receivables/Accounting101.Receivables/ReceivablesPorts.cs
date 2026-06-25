namespace Accounting101.Receivables;

/// <summary>The module's customer store — reference data in the client's database, via the engine's document store.</summary>
public interface ICustomerStore
{
    Task SaveAsync(Guid clientId, Customer customer, CancellationToken cancellationToken = default);
    Task<Customer?> GetAsync(Guid clientId, Guid customerId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The module's invoice store — evidentiary data with a draft → issue → void lifecycle, backed by the
/// engine's document store. The store owns number assignment (at finalize) and status derivation; the
/// service orchestrates the ledger posting around it.
/// </summary>
public interface IInvoiceStore
{
    /// <summary>Create a draft invoice (no number, no ledger effect). Returns it with its assigned id.</summary>
    Task<Invoice> CreateDraftAsync(Guid clientId, InvoiceBody body, CancellationToken cancellationToken = default);

    /// <summary>Finalize (issue) a draft: assigns the gapless number, locks the document. Returns the issued invoice.</summary>
    Task<Invoice> FinalizeAsync(Guid clientId, Guid invoiceId, CancellationToken cancellationToken = default);

    /// <summary>Void an issued invoice (no successor).</summary>
    Task VoidAsync(Guid clientId, Guid invoiceId, CancellationToken cancellationToken = default);

    Task<Invoice?> GetAsync(Guid clientId, Guid invoiceId, CancellationToken cancellationToken = default);

    /// <summary>All of a customer's live invoices (drafts + issued; voided are hidden).</summary>
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
