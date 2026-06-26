namespace Accounting101.Banking.Cash;

/// <summary>The clerk-supplied inputs for a cash deposit (e.g. owner contribution, loan proceeds,
/// other non-invoice cash in). The clerk provides the non-cash lines; the module derives the
/// balancing Cash debit.</summary>
public record CashDepositBody(
    IReadOnlyList<CashLine> Lines,
    DateOnly Date,
    string? Reference,
    string? Memo);
