namespace Accounting101.Banking.Cash;

/// <summary>The clerk-supplied inputs for a cash disbursement (e.g. loan payment, insurance prepay,
/// income-tax payment, owner draw). The clerk provides the non-cash lines; the module derives the
/// balancing Cash credit.</summary>
public record CashDisbursementBody(
    IReadOnlyList<CashLine> Lines,
    DateOnly Date,
    string? Reference,
    string? Memo);
