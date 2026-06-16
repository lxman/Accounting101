using MongoDB.Bson.Serialization.Attributes;

namespace Accounting101.Ledger.Mongo.Documents;

/// <summary>
/// The maintained trial-balance projection for one client — a single document
/// (account count is bounded, so it stays well under the 16 MB cap). Keyed by
/// client; the map is accountId ("N" format) -&gt; debit-positive balance. This is
/// a rebuildable cache; the journal remains the source of truth.
/// </summary>
public sealed class ClientBalancesDocument
{
    [BsonId]
    public Guid ClientId { get; set; }

    /// <summary>accountId (GUID "N") -&gt; debit-positive balance (Decimal128).</summary>
    public Dictionary<string, decimal> Balances { get; set; } = new();
}
