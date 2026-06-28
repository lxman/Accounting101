namespace Accounting101.Banking.Reconciliation;

/// <summary>Stored body of a bank adjustment (Number/Status/Id derived).</summary>
public sealed record BankAdjustmentBody(
    Guid ReconciliationId, Guid CashAccountId, Guid OffsetAccountId,
    AdjustmentKind Kind, decimal Amount, DateOnly Date, string? Memo);
