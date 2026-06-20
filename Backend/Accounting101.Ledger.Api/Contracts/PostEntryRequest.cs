namespace Accounting101.Ledger.Api.Contracts;

/// <summary>
/// Wire contract for posting a journal entry. The client id comes from the route, never the body,
/// so it cannot be spoofed. Reference/Memo are accepted for forward-compatibility (wired with the
/// fuller command endpoint). <see cref="SourceRef"/>/<see cref="SourceType"/> are the back-link an
/// upstream module sets to tie this entry to the business document that produced it.
/// </summary>
public sealed record PostEntryRequest(
    Guid? Id,
    long SequenceNumber,
    DateOnly EffectiveDate,
    string? Reference,
    string? Memo,
    IReadOnlyList<PostLineRequest> Lines,
    Guid? SourceRef = null,
    string? SourceType = null);

/// <summary>
/// One posting line: which account, debit or credit, a signed amount, and any subledger dimensions —
/// a map of dimension type to the referenced entity id (e.g. { "Customer": "..." } on an A/R line).
/// </summary>
public sealed record PostLineRequest(
    Guid AccountId,
    string Direction,
    decimal Amount,
    IReadOnlyDictionary<string, Guid>? Dimensions = null);
