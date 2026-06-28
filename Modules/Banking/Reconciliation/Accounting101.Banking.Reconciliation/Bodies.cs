namespace Accounting101.Banking.Reconciliation;

/// <summary>Stored body of a bank statement (Number/Status/Id are derived, never sent).</summary>
public sealed record BankStatementBody(
    Guid CashAccountId, DateOnly StatementDate, decimal OpeningBalance, decimal ClosingBalance,
    IReadOnlyList<BankStatementLine> Lines);
