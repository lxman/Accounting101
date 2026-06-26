namespace Accounting101.Payroll;

/// <summary>An evidentiary record of a tax remittance — posted in one step, voidable, never drafted.
/// Number and status are derived from the engine's document envelope; the module stores no identity
/// of its own.</summary>
public sealed record TaxRemittance
{
    public required Guid Id { get; init; }
    public string? Number { get; init; }
    public required decimal WithholdingsAmount { get; init; }
    public required decimal TaxesAmount { get; init; }
    public required DateOnly PayDate { get; init; }
    public string? Memo { get; init; }
    public TaxRemittanceStatus Status { get; init; } = TaxRemittanceStatus.Posted;
}
