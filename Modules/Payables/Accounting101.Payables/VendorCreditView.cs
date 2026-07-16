namespace Accounting101.Payables;

/// <summary>A vendor credit application plus the bills it was applied to and the id of its posted journal
/// entry — what the vendor-credit detail endpoint returns. Allocations are folded from the GL posting
/// (Posted-only) and reuse the shared BillAllocationLine; a credit application applies existing credit fully,
/// so the allocations' total IS the amount (no unapplied remainder). JournalEntryId drills to the GL entry.</summary>
public sealed record VendorCreditView(
    VendorCreditApplication Credit,
    IReadOnlyList<BillAllocationLine> Allocations,
    Guid? JournalEntryId);
