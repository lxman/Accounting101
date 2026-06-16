using MongoDB.Bson.Serialization.Attributes;

namespace Accounting101.Ledger.Mongo;

/// <summary>
/// A period-close snapshot for one client: per-account balances as of the close date.
/// It is the opening balance for the next period and bounds rebuilds (replay only since
/// the latest checkpoint). Like the projection it's recomputable — re-aggregating the
/// frozen period must reproduce it (tamper-evidence).
/// </summary>
public sealed class CheckpointDocument
{
    [BsonId]
    public Guid Id { get; set; }

    public Guid ClientId { get; set; }

    /// <summary>Close date / period end (ISO yyyy-MM-dd). Includes entries effective on or before this date.</summary>
    public string AsOf { get; set; } = string.Empty;

    /// <summary>accountId (GUID "N") -&gt; balance at close (Decimal128).</summary>
    public Dictionary<string, decimal> Balances { get; set; } = new();

    public Guid ClosedBy { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime ClosedAt { get; set; }
}
