using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Accounting101.Ledger.Mongo;

/// <summary>
/// One append-only, hash-chained audit record: who did what, when, with a point-in-time
/// snapshot of the principal. The chain (PreviousHash -&gt; Hash) makes the log tamper-evident.
/// </summary>
public sealed class AuditRecordDocument
{
    [BsonId]
    public Guid Id { get; set; }

    public Guid ClientId { get; set; }

    /// <summary>Per-client position in the hash chain (1-based).</summary>
    public long Sequence { get; set; }

    /// <summary>The entry acted on, or null for client-level events (e.g. a period close).</summary>
    public Guid? EntryId { get; set; }

    /// <summary>The entry version this action produced.</summary>
    public int EntryVersion { get; set; }

    [BsonRepresentation(BsonType.String)]
    public AuditAction Action { get; set; }

    public ActorSnapshot Actor { get; set; } = new();

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime At { get; set; }

    public string? Reason { get; set; }

    /// <summary>Hash of the previous record in the client's chain (empty for the first).</summary>
    public string PreviousHash { get; set; } = string.Empty;

    /// <summary>Hash of this record's content together with <see cref="PreviousHash"/>.</summary>
    public string Hash { get; set; } = string.Empty;
}

/// <summary>A point-in-time snapshot of the acting principal, recorded verbatim.</summary>
public sealed class ActorSnapshot
{
    public Guid UserId { get; set; }
    public string? Name { get; set; }
    public List<ClaimDocument> Claims { get; set; } = [];
}

public sealed class ClaimDocument
{
    public string Type { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}
