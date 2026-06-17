namespace Accounting101.Ledger.Api.Contracts;

/// <summary>
/// Replace an active entry with a corrected one (the edit path). The replacement supersedes the
/// original (referenced by route); the original stays in the journal, superseded. The client id
/// comes from the route.
/// </summary>
public sealed record ReviseRequest(
    Guid? Id,
    long SequenceNumber,
    DateOnly EffectiveDate,
    string? Reference,
    string? Memo,
    string? Reason,
    IReadOnlyList<PostLineRequest> Lines);

/// <summary>Close a period: snapshot on-the-books balances as of this date and freeze through it.</summary>
public sealed record ClosePeriodRequest(DateOnly AsOf);

/// <summary>
/// Reverse a posted entry: book a negating entry dated <see cref="ReversalDate"/> (which must be in an
/// open period). Used for reversing accruals and for correcting a closed period without unfreezing it.
/// </summary>
public sealed record ReverseRequest(DateOnly ReversalDate, long SequenceNumber, string? Reason);

/// <summary>
/// Close a fiscal year: post a balanced closing entry that resets temporary accounts (revenue/expense)
/// into retained earnings, then freeze through <see cref="FiscalYearEnd"/>.
/// </summary>
public sealed record CloseYearRequest(DateOnly FiscalYearEnd, long ClosingSequenceNumber);
