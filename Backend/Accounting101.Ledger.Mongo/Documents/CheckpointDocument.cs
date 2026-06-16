using MongoDB.Bson.Serialization.Attributes;

namespace Accounting101.Ledger.Mongo;

/// <summary>
/// The single period-close checkpoint for a client: per-account balances as of the close
/// date. It is the freeze pointer and the opening balance for the open period. There is at
/// most one per client — each close replaces it. Close history lives in the audit log, and
/// any past period-end balance is recomputable from the journal (as-of aggregation), so
/// prior checkpoints would be redundant.
/// </summary>
public sealed class CheckpointDocument
{
    [BsonId]
    public Guid ClientId { get; set; }

    /// <summary>Close date / period end (ISO yyyy-MM-dd). Includes entries effective on or before this date.</summary>
    public string AsOf { get; set; } = string.Empty;

    /// <summary>accountId (GUID "N") -&gt; balance at close (Decimal128).</summary>
    public Dictionary<string, decimal> Balances { get; set; } = new();

    public Guid ClosedBy { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime ClosedAt { get; set; }
}
