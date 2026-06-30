using Accounting101.Settlement;

namespace Accounting101.Receivables;

/// <summary>An uncollectible invoice balance written off to bad-debt expense. A non-cash settlement.</summary>
public sealed record WriteOff
{
    public required Guid Id { get; init; }
    public required Guid CustomerId { get; init; }
    public required DateOnly Date { get; init; }
    public required IReadOnlyList<Allocation> Allocations { get; init; }
    public string? Memo { get; init; }
    public bool Voided { get; init; }
    public decimal Total => Allocations.Sum(a => a.Amount);
}

/// <summary>A credit note reducing invoice balances without cash, via contra-revenue.</summary>
public sealed record CreditNote
{
    public required Guid Id { get; init; }
    public required Guid CustomerId { get; init; }
    public required DateOnly Date { get; init; }
    public required IReadOnlyList<Allocation> Allocations { get; init; }
    public string? Memo { get; init; }
    public bool Voided { get; init; }
    public decimal Total => Allocations.Sum(a => a.Amount);
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
