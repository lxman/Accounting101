namespace Accounting101.Banking.Reconciliation;

/// <summary>A bank-only adjustment booked during reconciliation. Charge = a bank fee (reduces cash);
/// Credit = bank interest (increases cash).</summary>
public enum AdjustmentKind { Charge, Credit }

public enum BankAdjustmentStatus { Posted, Void }

/// <summary>An evidentiary record of a bank adjustment — posted in one step (PendingApproval entry),
/// voidable. Number/status derived from the engine's document envelope.</summary>
public sealed record BankAdjustment
{
    public required Guid Id { get; init; }
    public string? Number { get; init; }                 // "ADJ-{seq:D5}"
    public required Guid ReconciliationId { get; init; }
    public required Guid CashAccountId { get; init; }
    public required Guid OffsetAccountId { get; init; }
    public required AdjustmentKind Kind { get; init; }
    public required decimal Amount { get; init; }
    public required DateOnly Date { get; init; }
    public string? Memo { get; init; }
    public BankAdjustmentStatus Status { get; init; } = BankAdjustmentStatus.Posted;
}
