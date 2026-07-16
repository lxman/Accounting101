namespace Accounting101.Payables;

/// <summary>One bill an allocation-based AP document (a bill payment, and any future vendor-credit detail)
/// was applied to: the bill's id, its number (null if still a draft), and the amount applied to it.
/// Recovered from the document's GL entry lines (each allocation line carries a "Bill" dimension and the
/// allocated amount). The general Payables allocation-line type, mirroring Receivables' InvoiceAllocationLine.</summary>
public sealed record BillAllocationLine(Guid BillId, string? BillNumber, decimal Amount);
