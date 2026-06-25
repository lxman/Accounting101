using Accounting101.Receivables;
using Accounting101.Settlement;

namespace Accounting101.Receivables.Api;

/// <summary>Create a customer.</summary>
public sealed record CreateCustomerRequest(string Name, string? Email);

/// <summary>Draft an invoice. The line input reuses the domain <see cref="InvoiceLine"/> (its computed
/// Amount is ignored on input). Number/Status/Id are server-assigned and never sent.</summary>
public sealed record DraftInvoiceRequest(
    Guid CustomerId, IReadOnlyList<InvoiceLine> Lines, decimal TaxRate, DateOnly IssueDate, DateOnly? DueDate, string? Memo);

/// <summary>Void an issued invoice, with an optional reason.</summary>
public sealed record VoidInvoiceRequest(string? Reason);

/// <summary>Record a customer payment with its allocations across invoices.</summary>
public sealed record RecordPaymentRequest(
    Guid CustomerId, DateOnly Date, decimal Amount, string? Method, IReadOnlyList<Allocation> Allocations);

/// <summary>Apply a customer's existing credit to invoices.</summary>
public sealed record CreditApplicationRequest(
    Guid CustomerId, DateOnly Date, IReadOnlyList<Allocation> Allocations);
