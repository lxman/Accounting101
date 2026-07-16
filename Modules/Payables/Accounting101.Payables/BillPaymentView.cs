namespace Accounting101.Payables;

/// <summary>A vendor bill payment plus the bills it was applied to, the unapplied remainder held as vendor
/// credit, and the id of its posted journal entry — what the bill-payment detail endpoint returns.
/// Allocations are folded from the GL posting (Posted-only); Unapplied = Amount − Σallocations (the
/// overpayment held as credit); JournalEntryId lets the UI drill to the GL entry (null if none found).</summary>
public sealed record BillPaymentView(
    BillPayment Payment,
    IReadOnlyList<BillAllocationLine> Allocations,
    decimal Unapplied,
    Guid? JournalEntryId);
