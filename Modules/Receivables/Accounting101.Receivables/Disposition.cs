namespace Accounting101.Receivables;

/// <summary>An uncollectible invoice balance written off to bad-debt expense. A non-cash settlement. The
/// per-invoice split is no longer stored here — it lives only as ledger dimensions on this document's
/// entry; callers that need the total fold it from that entry (see <see cref="SettlementRelief"/>).</summary>
public sealed record WriteOff
{
    public required Guid Id { get; init; }
    public required Guid CustomerId { get; init; }
    public required DateOnly Date { get; init; }
    public string? Memo { get; init; }
    public bool Voided { get; init; }
}

/// <summary>A credit note reducing invoice balances without cash, via contra-revenue. The per-invoice split
/// is no longer stored here — it lives only as ledger dimensions on this document's entry.</summary>
public sealed record CreditNote
{
    public required Guid Id { get; init; }
    public required Guid CustomerId { get; init; }
    public required DateOnly Date { get; init; }
    public string? Memo { get; init; }
    public bool Voided { get; init; }
}

/// <summary>Cash paid back to a customer against their unapplied credit balance.</summary>
public sealed record Refund
{
    public required Guid Id { get; init; }
    public required Guid CustomerId { get; init; }
    public required DateOnly Date { get; init; }
    public required decimal Amount { get; init; }
    public string? Memo { get; init; }
    public bool Voided { get; init; }
}
