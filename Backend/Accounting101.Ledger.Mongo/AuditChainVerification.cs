namespace Accounting101.Ledger.Mongo;

/// <summary>How a client's audit chain failed verification. Mapped 1:1 from the checks in
/// <see cref="MongoAuditLog.VerifyDetailedAsync"/>.</summary>
public enum AuditChainFailure
{
    /// <summary>A record's sequence is not the expected next value (a record is missing / non-contiguous).</summary>
    SequenceGap,
    /// <summary>A record's PreviousHash does not match its predecessor's hash.</summary>
    BrokenLink,
    /// <summary>A record's stored hash does not recompute from its content (the record was edited).</summary>
    HashMismatch,
    /// <summary>The walk is internally clean but the guarded head remembers records past the chain tail
    /// (the newest N records were deleted).</summary>
    TailTruncated,
    /// <summary>The walk is clean and the head sequence matches the tail, but the head hash does not
    /// (or the head is missing though records exist).</summary>
    HeadMismatch,
}

/// <summary>The detailed result of verifying a client's audit chain: whether it is intact, how many
/// records were walked, the guarded head sequence, and — when broken — the failure kind and the
/// sequence at which it was detected.</summary>
public sealed record AuditChainVerification(
    bool Valid, long RecordCount, long? HeadSequence, AuditChainFailure? Failure, long? BrokenAtSequence);
