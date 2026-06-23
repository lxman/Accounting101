using Accounting101.Invoicing;

namespace Accounting101.Invoicing.Api;

/// <summary>Create a customer.</summary>
public sealed record CreateCustomerRequest(string Name, string? Email);

/// <summary>Draft an invoice. The line input reuses the domain <see cref="InvoiceLine"/> (its computed
/// Amount is ignored on input). Number/Status/Id are server-assigned and never sent.</summary>
public sealed record DraftInvoiceRequest(
    Guid CustomerId, IReadOnlyList<InvoiceLine> Lines, decimal TaxRate, DateOnly IssueDate, DateOnly? DueDate, string? Memo);

/// <summary>Void an issued invoice, with an optional reason.</summary>
public sealed record VoidInvoiceRequest(string? Reason);
