using Accounting101.Settlement;

namespace Accounting101.Payables;

/// <summary>A recorded payment to a vendor. Voided is derived from the document lifecycle.</summary>
public sealed record BillPayment
{
    public required Guid Id { get; init; }
    public required Guid VendorId { get; init; }
    public required DateOnly Date { get; init; }
    public required decimal Amount { get; init; }
    public string? Method { get; init; }
    public required IReadOnlyList<Allocation> Allocations { get; init; }
    public bool Voided { get; init; }

    public decimal Allocated => Allocations.Sum(a => a.Amount);
    public decimal Unapplied => Amount - Allocated;
}

/// <summary>An application of existing vendor credit to bills.</summary>
public sealed record VendorCreditApplication
{
    public required Guid Id { get; init; }
    public required Guid VendorId { get; init; }
    public required DateOnly Date { get; init; }
    public required IReadOnlyList<Allocation> Allocations { get; init; }
    public bool Voided { get; init; }

    public decimal Applied => Allocations.Sum(a => a.Amount);
}
