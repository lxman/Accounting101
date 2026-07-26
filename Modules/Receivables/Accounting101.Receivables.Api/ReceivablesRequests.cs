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

/// <summary>Record a customer payment with its allocations across invoices. <paramref name="Id"/> is an
/// optional caller-supplied retry key — same key and same content replays, different content is a 422.</summary>
public sealed record RecordPaymentRequest(
    Guid CustomerId, DateOnly Date, decimal Amount, string? Method, IReadOnlyList<Allocation> Allocations,
    Guid? Id = null);

/// <summary>Apply a customer's existing credit to invoices. <paramref name="Id"/> is an optional
/// caller-supplied retry key — same key and same content replays, different content is a 422.</summary>
public sealed record CreditApplicationRequest(
    Guid CustomerId, DateOnly Date, IReadOnlyList<Allocation> Allocations, Guid? Id = null);

/// <summary>Write off invoice balances to bad-debt expense.</summary>
public sealed record WriteOffRequest(Guid CustomerId, DateOnly Date, IReadOnlyList<Allocation> Allocations, string? Memo);

/// <summary>Issue a credit note against invoices (contra-revenue, no cash).</summary>
public sealed record CreditNoteRequest(Guid CustomerId, DateOnly Date, IReadOnlyList<Allocation> Allocations, string? Memo);

/// <summary>Refund a customer's unapplied credit as cash.</summary>
public sealed record RefundRequest(Guid CustomerId, DateOnly Date, decimal Amount, string? Memo);
