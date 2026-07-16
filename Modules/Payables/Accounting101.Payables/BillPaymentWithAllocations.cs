using Accounting101.Settlement;

namespace Accounting101.Payables;

/// <summary>A bill payment as the Payments list reads it: the stored document fields plus the per-bill
/// allocations folded from its GL posting (Posted-only). The module stores no allocation array; this is the
/// read shape the list + bill-detail "applied payments" section consume (each Allocation is {bill id, amount}).</summary>
public sealed record BillPaymentWithAllocations(
    Guid Id, Guid VendorId, DateOnly Date, decimal Amount, string? Method, bool Voided,
    IReadOnlyList<Allocation> Allocations);
