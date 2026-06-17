namespace Accounting101.Ledger.Api.Contracts;

public sealed record PostEntryResponse(Guid Id, string Status, string Posting);

public sealed record EntryResponse(
    Guid Id,
    long SequenceNumber,
    DateOnly EffectiveDate,
    string Type,
    string Status,
    string Posting,
    int LineCount,
    Guid? Supersedes,
    Guid? SupersededBy,
    Guid? ReversalOf,
    Guid? ReversedBy);

public sealed record AuditRecordResponse(
    long Sequence,
    string Action,
    Guid? EntryId,
    int EntryVersion,
    DateTimeOffset At,
    string? Reason,
    ActorResponse Actor);

public sealed record ActorResponse(Guid UserId, string? Name, IReadOnlyList<ClaimResponse> Claims);

public sealed record ClaimResponse(string Type, string Value);

public sealed record AccountBalanceResponse(Guid AccountId, decimal Balance);

public sealed record TrialBalanceResponse(DateOnly? AsOf, IReadOnlyList<AccountBalanceResponse> Accounts);

public sealed record CloseResponse(DateOnly AsOf, IReadOnlyList<AccountBalanceResponse> OpeningBalances);

public sealed record AuditVerifyResponse(bool Valid);
