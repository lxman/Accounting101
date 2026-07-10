using Accounting101.Settlement;

namespace Accounting101.Payables;

/// <summary>Stored body of a bill payment — carries no allocation array (the per-bill split lives as ledger
/// dimensions; see BillPaymentCommand for the write-path request).</summary>
public sealed record BillPaymentBody(Guid VendorId, DateOnly Date, decimal Amount, string? Method);

/// <summary>What RecordPaymentAsync accepts: BillPaymentBody plus the caller's per-bill allocations, consumed
/// into ledger dimensions at compose time and never persisted.</summary>
public sealed record BillPaymentCommand(
    Guid VendorId, DateOnly Date, decimal Amount, string? Method, IReadOnlyList<Allocation> Allocations);

/// <summary>Stored body of a vendor-credit application — carries no allocation array; see command.</summary>
public sealed record VendorCreditApplicationBody(Guid VendorId, DateOnly Date);

public sealed record VendorCreditApplicationCommand(
    Guid VendorId, DateOnly Date, IReadOnlyList<Allocation> Allocations);
