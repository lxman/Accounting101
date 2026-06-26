namespace Accounting101.Banking.Cash;

/// <summary>An evidentiary record of a cash deposit — posted in one step, voidable, never drafted.
/// Number and status are derived from the engine's document envelope; the module stores no identity
/// of its own.</summary>
public sealed record CashDeposit
{
    public required Guid Id { get; init; }
    public string? Number { get; init; }
    public required IReadOnlyList<CashLine> Lines { get; init; }
    public required DateOnly Date { get; init; }
    public string? Reference { get; init; }
    public string? Memo { get; init; }
    public CashDepositStatus Status { get; init; } = CashDepositStatus.Posted;
}
