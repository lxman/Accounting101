namespace Accounting101.Ledger.Api.Contracts;

/// <summary>
/// Wire contract for posting a journal entry. The client id comes from the route, never the body,
/// so it cannot be spoofed. Reference/Memo are accepted for forward-compatibility (wired with the
/// fuller command endpoint).
/// </summary>
public sealed record PostEntryRequest(
    Guid? Id,
    long SequenceNumber,
    DateOnly EffectiveDate,
    string? Reference,
    string? Memo,
    IReadOnlyList<PostLineRequest> Lines);

/// <summary>One posting line: which account, debit or credit, and a signed amount.</summary>
public sealed record PostLineRequest(Guid AccountId, string Direction, decimal Amount);
