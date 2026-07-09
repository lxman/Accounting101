using Accounting101.Settlement;

namespace Accounting101.Receivables;

/// <summary>Persisted body of a payment — cash received from a customer. Carries no allocation array: the
/// per-invoice split lives only as ledger dimensions on the payment's entry (see <see cref="PaymentCommand"/>
/// for the write-path request that still carries allocations through to the posting recipe).</summary>
public sealed record PaymentBody(Guid CustomerId, DateOnly Date, decimal Amount, string? Method);

/// <summary>What <see cref="PaymentService.RecordPaymentAsync"/> accepts: everything in <see cref="PaymentBody"/>
/// plus the caller's choice of per-invoice allocations. The allocations are consumed into ledger dimensions
/// at compose time and never persisted.</summary>
public sealed record PaymentCommand(
    Guid CustomerId, DateOnly Date, decimal Amount, string? Method, IReadOnlyList<Allocation> Allocations);

/// <summary>Persisted body of a credit application — existing customer credit applied to invoices (no
/// cash). Carries no allocation array; see <see cref="CreditApplicationCommand"/>.</summary>
public sealed record CreditApplicationBody(Guid CustomerId, DateOnly Date);

/// <summary>What <see cref="PaymentService.RecordCreditApplicationAsync"/> accepts: <see cref="CreditApplicationBody"/>
/// plus the caller's choice of per-invoice allocations.</summary>
public sealed record CreditApplicationCommand(
    Guid CustomerId, DateOnly Date, IReadOnlyList<Allocation> Allocations);
