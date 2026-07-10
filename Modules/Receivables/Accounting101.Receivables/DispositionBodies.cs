using Accounting101.Settlement;

namespace Accounting101.Receivables;

/// <summary>Persisted body of a write-off — an uncollectible invoice balance sent to bad-debt expense.
/// Carries no allocation array; see <see cref="WriteOffCommand"/> for the write-path request.</summary>
public sealed record WriteOffBody(Guid CustomerId, DateOnly Date, string? Memo);

/// <summary>What <see cref="PaymentService.RecordWriteOffAsync"/> accepts: <see cref="WriteOffBody"/> plus
/// the caller's choice of per-invoice allocations (their sum equals the amount written off — no unapplied
/// remainder). Consumed into ledger dimensions at compose time and never persisted.</summary>
public sealed record WriteOffCommand(Guid CustomerId, DateOnly Date, IReadOnlyList<Allocation> Allocations, string? Memo);

/// <summary>Persisted body of a credit note — an invoice balance reduced without cash, via contra-revenue.
/// Carries no allocation array; see <see cref="CreditNoteCommand"/>.</summary>
public sealed record CreditNoteBody(Guid CustomerId, DateOnly Date, string? Memo);

/// <summary>What <see cref="PaymentService.RecordCreditNoteAsync"/> accepts: <see cref="CreditNoteBody"/>
/// plus the caller's choice of per-invoice allocations.</summary>
public sealed record CreditNoteCommand(Guid CustomerId, DateOnly Date, IReadOnlyList<Allocation> Allocations, string? Memo);

/// <summary>Pay a customer's unapplied credit balance back as cash. Unaffected by the allocation-storage
/// removal — a refund never carries per-invoice allocations.</summary>
public sealed record RefundBody(Guid CustomerId, DateOnly Date, decimal Amount, string? Memo);
