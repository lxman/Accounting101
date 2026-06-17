namespace Accounting101.Ledger.Api.Contracts;

public sealed record PostEntryResponse(Guid Id, string Status, string Posting);

public sealed record EntryResponse(
    Guid Id,
    long SequenceNumber,
    DateOnly EffectiveDate,
    string Type,
    string Status,
    string Posting,
    int LineCount);

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
