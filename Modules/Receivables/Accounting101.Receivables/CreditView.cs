namespace Accounting101.Receivables;

/// <summary>A credit (note, write-off, or application) plus the invoices it was applied to and the id of
/// its posted journal entry — what the credit detail endpoint returns. Credit reuses the unified
/// CreditDocument shape so the detail header matches the list row; Allocations are folded from the GL
/// posting; JournalEntryId lets the UI drill to the GL entry (null if none is found).</summary>
public sealed record CreditView(
    CreditDocument Credit,
    IReadOnlyList<InvoiceAllocationLine> Allocations,
    Guid? JournalEntryId);
