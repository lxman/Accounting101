namespace Accounting101.Receivables;

/// <summary>One invoice an allocation-based document (credit or payment) was applied to: the invoice's id,
/// its number (null if unnumbered), and the amount applied to it. Recovered from the document's GL entry
/// lines (each allocation line carries an "Invoice" dimension and the allocated amount).</summary>
public sealed record InvoiceAllocationLine(Guid InvoiceId, string? InvoiceNumber, decimal Amount);
