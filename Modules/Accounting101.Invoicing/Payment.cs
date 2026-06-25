namespace Accounting101.Invoicing;

/// <summary>A recorded customer payment. Voided is derived from the document lifecycle.</summary>
public sealed record Payment
{
    public required Guid Id { get; init; }
    public required Guid CustomerId { get; init; }
    public required DateOnly Date { get; init; }
    public required decimal Amount { get; init; }
    public string? Method { get; init; }
    public required IReadOnlyList<Allocation> Allocations { get; init; }
    public bool Voided { get; init; }

    public decimal Allocated => Allocations.Sum(a => a.Amount);
    public decimal Unapplied => Amount - Allocated;
}

/// <summary>An application of existing customer credit to invoices.</summary>
public sealed record CreditApplication
{
    public required Guid Id { get; init; }
    public required Guid CustomerId { get; init; }
    public required DateOnly Date { get; init; }
    public required IReadOnlyList<Allocation> Allocations { get; init; }
    public bool Voided { get; init; }

    public decimal Applied => Allocations.Sum(a => a.Amount);
}
