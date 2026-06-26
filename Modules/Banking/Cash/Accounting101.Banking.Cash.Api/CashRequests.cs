namespace Accounting101.Banking.Cash.Api;

/// <summary>Record a cash disbursement (loan payment, insurance prepay, income-tax payment, owner draw,
/// etc.). The clerk supplies the non-cash lines; the module derives the balancing Cash credit.
/// Id/Number/Status are server-assigned and never sent.</summary>
public sealed record RecordCashDisbursementRequest(
    IReadOnlyList<CashLineRequest> Lines,
    DateOnly Date,
    string? Reference,
    string? Memo);

/// <summary>Record a cash deposit (owner contribution, loan proceeds, other non-invoice cash in).
/// The clerk supplies the non-cash lines; the module derives the balancing Cash debit.
/// Id/Number/Status are server-assigned and never sent.</summary>
public sealed record RecordCashDepositRequest(
    IReadOnlyList<CashLineRequest> Lines,
    DateOnly Date,
    string? Reference,
    string? Memo);

/// <summary>A single line on a cash voucher request: which account is affected and by how much.</summary>
public sealed record CashLineRequest(Guid AccountId, decimal Amount);

/// <summary>Void a posted cash disbursement or deposit, with an optional reason.</summary>
public sealed record VoidReasonRequest(string? Reason);
