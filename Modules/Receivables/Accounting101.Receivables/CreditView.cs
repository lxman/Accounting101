namespace Accounting101.Receivables;

/// <summary>One invoice a credit was applied to: the invoice's id, its number (null if unnumbered),
/// and the amount of this credit applied to it. Recovered from the credit's GL entry lines (each
/// allocation line carries an "Invoice" dimension and the allocated amount).</summary>
public sealed record CreditAllocationLine(Guid InvoiceId, string? InvoiceNumber, decimal Amount);

/// <summary>A credit (note, write-off, or application) plus the invoices it was applied to and the id of
/// its posted journal entry — what the credit detail endpoint returns. Credit reuses the unified
/// CreditDocument shape so the detail header matches the list row; Allocations are folded from the GL
/// posting; JournalEntryId lets the UI drill to the GL entry (null if none is found).</summary>
public sealed record CreditView(
    CreditDocument Credit,
    IReadOnlyList<CreditAllocationLine> Allocations,
    Guid? JournalEntryId);
