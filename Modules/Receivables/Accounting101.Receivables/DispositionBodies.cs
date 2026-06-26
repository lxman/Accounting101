using Accounting101.Settlement;

namespace Accounting101.Receivables;

/// <summary>Write off an uncollectible invoice balance to bad-debt expense. Allocations target the invoices
/// being cleared; the amount written off equals their sum (no unapplied remainder).</summary>
public sealed record WriteOffBody(Guid CustomerId, DateOnly Date, IReadOnlyList<Allocation> Allocations, string? Memo);

/// <summary>Reduce an invoice balance without cash (return/billing adjustment), via contra-revenue.</summary>
public sealed record CreditNoteBody(Guid CustomerId, DateOnly Date, IReadOnlyList<Allocation> Allocations, string? Memo);

/// <summary>Pay a customer's unapplied credit balance back as cash.</summary>
public sealed record RefundBody(Guid CustomerId, DateOnly Date, decimal Amount, string? Memo);
