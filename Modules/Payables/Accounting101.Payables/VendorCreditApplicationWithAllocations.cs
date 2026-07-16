using Accounting101.Settlement;

namespace Accounting101.Payables;

/// <summary>A vendor credit application as the Credits list reads it: the stored document fields plus the
/// per-bill allocations folded from its GL posting (Posted-only). The module stores no allocation array;
/// this is the read shape the list consumes (each Allocation is {bill id, amount}).</summary>
public sealed record VendorCreditApplicationWithAllocations(
    Guid Id, Guid VendorId, DateOnly Date, bool Voided,
    IReadOnlyList<Allocation> Allocations);
