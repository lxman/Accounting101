using System.Globalization;
using Accounting101.Ledger.Core.Journal;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Accounting101.Ledger.Mongo.Documents;

/// <summary>
/// Mongo storage shape for a <see cref="JournalEntry"/> — one document per entry.
/// Enums are stored as strings; the effective (accounting) date as an ISO
/// <c>yyyy-MM-dd</c> string (sorts chronologically); timestamps in UTC. Reads go
/// back through <see cref="JournalEntry.Create"/>, so a load re-validates the
/// balance invariant.
/// </summary>
public sealed class JournalEntryDocument
{
    private const string DateFormat = "yyyy-MM-dd";

    [BsonId]
    public Guid Id { get; set; }

    public Guid ClientId { get; set; }
    public long SequenceNumber { get; set; }

    /// <summary>Accounting date as ISO <c>yyyy-MM-dd</c>.</summary>
    public string EffectiveDate { get; set; } = string.Empty;

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime PostedAt { get; set; }

    [BsonRepresentation(BsonType.String)]
    public EntryType Type { get; set; }

    [BsonRepresentation(BsonType.String)]
    public LifecycleStatus Status { get; set; }

    [BsonRepresentation(BsonType.String)]
    public PostingState Posting { get; set; }

    public Guid? Supersedes { get; set; }
    public Guid? SupersededBy { get; set; }
    public Guid? ReversalOf { get; set; }
    public Guid? ReversedBy { get; set; }
    public Guid? SourceRef { get; set; }
    public string? SourceType { get; set; }
    public string? Reference { get; set; }
    public string? Memo { get; set; }
    public int Version { get; set; }

    public AuditStampDocument Audit { get; set; } = new();
    public List<LineDocument> Lines { get; set; } = [];

    public static JournalEntryDocument FromDomain(JournalEntry e) => new()
    {
        Id = e.Id,
        ClientId = e.ClientId,
        SequenceNumber = e.SequenceNumber,
        EffectiveDate = e.EffectiveDate.ToString(DateFormat, CultureInfo.InvariantCulture),
        PostedAt = e.PostedAt.UtcDateTime,
        Type = e.Type,
        Status = e.Status,
        Posting = e.Posting,
        Supersedes = e.Supersedes,
        SupersededBy = e.SupersededBy,
        ReversalOf = e.ReversalOf,
        ReversedBy = e.ReversedBy,
        SourceRef = e.SourceRef,
        SourceType = e.SourceType,
        Reference = e.Reference,
        Memo = e.Memo,
        Version = e.Version,
        Audit = AuditStampDocument.FromDomain(e.Audit),
        Lines = e.Lines.Select(LineDocument.FromDomain).ToList(),
    };

    public JournalEntry ToDomain() => JournalEntry.Create(
        id: Id,
        clientId: ClientId,
        sequenceNumber: SequenceNumber,
        effectiveDate: DateOnly.ParseExact(EffectiveDate, DateFormat, CultureInfo.InvariantCulture),
        postedAt: new DateTimeOffset(DateTime.SpecifyKind(PostedAt, DateTimeKind.Utc), TimeSpan.Zero),
        type: Type,
        audit: Audit.ToDomain(),
        lines: Lines.Select(l => l.ToDomain()).ToList(),
        posting: Posting,
        status: Status,
        version: Version,
        supersedes: Supersedes,
        supersededBy: SupersededBy,
        reversalOf: ReversalOf,
        reversedBy: ReversedBy,
        sourceRef: SourceRef,
        sourceType: SourceType,
        reference: Reference,
        memo: Memo);
}
