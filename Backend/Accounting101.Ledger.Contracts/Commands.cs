namespace Accounting101.Ledger.Contracts;

/// <summary>
/// Replace an active entry with a corrected one (the edit path). The replacement supersedes the
/// original (referenced by route); the original stays in the journal, superseded. The client id
/// comes from the route.
/// </summary>
public sealed record ReviseRequest(
    Guid? Id,
    DateOnly EffectiveDate,
    string? Reference,
    string? Memo,
    string? Reason,
    IReadOnlyList<PostLineRequest> Lines,
    Guid? SourceRef = null,
    string? SourceType = null,
    string? Type = null); // "Standard" (default) or "Adjusting"; wire field is `type` (matches EntryResponse.Type)

/// <summary>Close a period: snapshot on-the-books balances as of this date and freeze through it.</summary>
public sealed record ClosePeriodRequest(DateOnly AsOf);

/// <summary>Void an active entry (delete-as-event), with an optional reason for the audit log.</summary>
public sealed record VoidRequest(string? Reason);

/// <summary>
/// Reverse a posted entry: book a negating entry dated <see cref="ReversalDate"/> (which must be in an
/// open period). Used for reversing accruals and for correcting a closed period without unfreezing it.
/// </summary>
public sealed record ReverseRequest(DateOnly ReversalDate, string? Reason);

/// <summary>
/// Close a fiscal year: post a balanced closing entry that resets temporary accounts (revenue/expense)
/// into retained earnings, then freeze through <see cref="FiscalYearEnd"/>.
/// </summary>
public sealed record CloseYearRequest(DateOnly FiscalYearEnd);

/// <summary>
/// Reopen a closed period: move the freeze pointer back to <see cref="ReopenThrough"/>, or null to clear
/// it entirely. The most privileged period action — admin-only and gated by step-up re-auth.
/// </summary>
public sealed record ReopenRequest(DateOnly? ReopenThrough, string? Reason);

/// <summary>
/// Establish opening balances at a cutover date as one balanced Opening entry. Balances are signed
/// debit-positive (a debit balance positive, a credit balance negative) and must net to zero.
/// </summary>
public sealed record OnboardingRequest(DateOnly AsOf, IReadOnlyList<OpeningBalanceLine> Balances);

/// <summary>One account's carried-in balance (signed, debit-positive), with any required dimensions.</summary>
public sealed record OpeningBalanceLine(
    Guid AccountId,
    decimal Balance,
    IReadOnlyDictionary<string, Guid>? Dimensions = null);
