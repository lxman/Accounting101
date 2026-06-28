namespace Accounting101.Banking.Reconciliation;

/// <summary>One ledger entry on the reconciliation worksheet.</summary>
public sealed record WorksheetEntry(Guid EntryId, DateOnly Date, string? Reference, string? SourceType, decimal CashEffect, bool Cleared);

/// <summary>The reconciliation worksheet: the statement, the cash-account entries through the statement date
/// with cleared flags, and the cleared-method totals + verdict.</summary>
public sealed record ReconciliationWorksheet(
    Reconciliation Reconciliation,
    BankStatement Statement,
    IReadOnlyList<WorksheetEntry> Entries,
    decimal BookBalance,
    decimal ClearedTotal,
    decimal ReconciledDifference,
    bool Balanced);
