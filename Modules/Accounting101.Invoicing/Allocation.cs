namespace Accounting101.Invoicing;

/// <summary>The atom that reduces an invoice's open balance: an amount applied to one invoice,
/// regardless of the funding document.</summary>
public sealed record Allocation(Guid InvoiceId, decimal Amount);
