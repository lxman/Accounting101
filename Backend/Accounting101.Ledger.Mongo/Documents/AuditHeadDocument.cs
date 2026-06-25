using MongoDB.Bson.Serialization.Attributes;

namespace Accounting101.Ledger.Mongo.Documents;

/// <summary>
/// Per-client guarded chain-head: the expected latest (Sequence, Hash) of the audit chain.
/// VerifyAsync reconciles the walked chain against this so a truncated tail is detected.
/// </summary>
public sealed class AuditHeadDocument
{
    [BsonId] public Guid ClientId { get; set; }
    public long Sequence { get; set; }
    public string Hash { get; set; } = string.Empty;
}
