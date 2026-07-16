using Accounting101.Settlement;

namespace Accounting101.Receivables;

/// <summary>A payment as the Payments list reads it: the stored document fields plus the per-invoice
/// allocations folded from its GL posting (Posted-only). The module stores no allocation array; this is
/// the read shape the list + invoice-detail "applied payments" section consume (each Allocation is
/// {invoice id, amount}).</summary>
public sealed record PaymentWithAllocations(
    Guid Id, Guid CustomerId, DateOnly Date, decimal Amount, string? Method, bool Voided,
    IReadOnlyList<Allocation> Allocations);
