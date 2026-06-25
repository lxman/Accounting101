using Accounting101.Settlement;

namespace Accounting101.Payables;

/// <summary>Stored body of a bill payment — cash paid to a vendor, with its allocations across bills.
/// Allocations may sum to less than Amount; the remainder becomes vendor credit (a prepayment).</summary>
public sealed record BillPaymentBody(
    Guid VendorId, DateOnly Date, decimal Amount, string? Method, IReadOnlyList<Allocation> Allocations);

/// <summary>Stored body of a vendor-credit application — existing vendor credit applied to bills (no cash).</summary>
public sealed record VendorCreditApplicationBody(
    Guid VendorId, DateOnly Date, IReadOnlyList<Allocation> Allocations);
