using Accounting101.Ledger.Core.Journal;
using MongoDB.Bson.Serialization.Attributes;

namespace Accounting101.Ledger.Mongo.Documents;

/// <summary>Mongo storage shape for <see cref="AuditStamp"/>. Timestamps are stored in UTC.</summary>
public sealed class AuditStampDocument
{
    public Guid CreatedBy { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAt { get; set; }

    public Guid? PostedBy { get; set; }
    public Guid? ApprovedBy { get; set; }

    public static AuditStampDocument FromDomain(AuditStamp a) => new()
    {
        CreatedBy = a.CreatedBy,
        CreatedAt = a.CreatedAt.UtcDateTime,
        PostedBy = a.PostedBy,
        ApprovedBy = a.ApprovedBy,
    };

    public AuditStamp ToDomain() => new()
    {
        CreatedBy = CreatedBy,
        CreatedAt = new DateTimeOffset(DateTime.SpecifyKind(CreatedAt, DateTimeKind.Utc), TimeSpan.Zero),
        PostedBy = PostedBy,
        ApprovedBy = ApprovedBy,
    };
}
