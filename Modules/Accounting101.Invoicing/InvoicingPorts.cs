namespace Accounting101.Invoicing;

/// <summary>The module's customer store — its own collection in the client's database.</summary>
public interface ICustomerStore
{
    Task SaveAsync(Guid clientId, Customer customer, CancellationToken cancellationToken = default);
    Task<Customer?> GetAsync(Guid clientId, Guid customerId, CancellationToken cancellationToken = default);
}

/// <summary>The module's invoice store — its own collection in the client's database.</summary>
public interface IInvoiceStore
{
    Task SaveAsync(Guid clientId, Invoice invoice, CancellationToken cancellationToken = default);
    Task<Invoice?> GetAsync(Guid clientId, Guid invoiceId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The module's own invoice-number sequence — distinct from the engine's journal sequence. Each module
/// owns its document numbering (an atomic counter in the client's database), so two modules never collide.
/// </summary>
public interface IInvoiceNumbers
{
    Task<string> NextAsync(Guid clientId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves the chart accounts the invoicing recipe posts to for a given client — the module's chart
/// contract. The accounts differ per client, so this is resolved per call, not configured once.
/// </summary>
public interface IInvoiceAccountsProvider
{
    Task<InvoicePostingAccounts> GetAsync(Guid clientId, CancellationToken cancellationToken = default);
}
