namespace Accounting101.Payables;

/// <summary>One line of a bill: an expense amount coded to a specific expense account.</summary>
public sealed record BillLine
{
    public required string Description { get; init; }
    public required decimal Amount { get; init; }
    public required Guid ExpenseAccountId { get; init; }
}

/// <summary>A vendor bill: the commercial document the payables module owns. Its money rolls up into one
/// balanced journal entry when the bill is entered; the per-line detail stays here.</summary>
public sealed record Bill
{
    public required Guid Id { get; init; }
    public required Guid VendorId { get; init; }
    /// <summary>Internal bill number from the engine's gapless sequence at enter; null while a draft.</summary>
    public string? Number { get; init; }
    public required DateOnly BillDate { get; init; }
    public DateOnly? DueDate { get; init; }
    /// <summary>The vendor's own invoice number (external reference).</summary>
    public string? VendorReference { get; init; }
    public string? Memo { get; init; }
    public BillStatus Status { get; init; } = BillStatus.Draft;
    public required IReadOnlyList<BillLine> Lines { get; init; }

    public decimal Total => Lines.Sum(l => l.Amount);
}
