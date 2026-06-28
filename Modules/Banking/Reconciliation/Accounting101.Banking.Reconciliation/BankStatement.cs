namespace Accounting101.Banking.Reconciliation;

/// <summary>A bank statement: immutable evidence of what the bank reported for a cash account over a period.</summary>
public sealed record BankStatement
{
    public required Guid Id { get; init; }
    public string? Number { get; init; }               // "BST-{seq:D5}", assigned at finalize
    public required Guid CashAccountId { get; init; }
    public required DateOnly StatementDate { get; init; }
    public required decimal OpeningBalance { get; init; }
    public required decimal ClosingBalance { get; init; }
    public required IReadOnlyList<BankStatementLine> Lines { get; init; }
    public BankStatementStatus Status { get; init; } = BankStatementStatus.Posted;
}

/// <summary>One line on a bank statement. Amount is signed from the bank's perspective: + money into the
/// account (a deposit clearing), − money out (a payment clearing).</summary>
public sealed record BankStatementLine(DateOnly Date, decimal Amount, string Description, string? ExternalRef);

public enum BankStatementStatus { Posted, Void }
