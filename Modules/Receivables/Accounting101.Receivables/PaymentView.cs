namespace Accounting101.Receivables;

/// <summary>A customer payment plus the invoices it was applied to, the unapplied remainder held as
/// customer credit, and the id of its posted journal entry — what the payment detail endpoint returns.
/// Allocations are folded from the GL posting (Posted-only); Unapplied = Amount − Σallocations (the
/// overpayment held as credit); JournalEntryId lets the UI drill to the GL entry (null if none found).</summary>
public sealed record PaymentView(
    Payment Payment,
    IReadOnlyList<InvoiceAllocationLine> Allocations,
    decimal Unapplied,
    Guid? JournalEntryId);
