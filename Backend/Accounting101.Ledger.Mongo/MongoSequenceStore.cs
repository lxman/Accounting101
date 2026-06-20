using MongoDB.Bson;
using MongoDB.Driver;

namespace Accounting101.Ledger.Mongo;

/// <summary>
/// Atomic, named sequence generators — Mongo's equivalent of a SQL SEQUENCE. One document per named
/// counter, bumped with a single <c>findAndModify</c> <c>$inc</c>, so concurrent allocations never
/// collide and never block beyond the per-document write. The engine owns the per-client journal
/// counter; a module owns its own counters collection for its document numbers.
/// </summary>
public sealed class MongoSequenceStore
{
    private readonly IMongoCollection<BsonDocument> _counters;

    public MongoSequenceStore(IMongoDatabase database, string collectionName = "counters")
    {
        ArgumentNullException.ThrowIfNull(database);
        _counters = database.GetCollection<BsonDocument>(collectionName);
    }

    /// <summary>
    /// Allocate the next journal sequence number for a client. Joins the caller's transaction, so the
    /// increment commits together with the entry insert — if the post rolls back, so does the number,
    /// keeping the per-client sequence gapless. The first allocation returns 1.
    /// </summary>
    public async Task<long> NextJournalAsync(
        Guid clientId, IClientSessionHandle session, CancellationToken cancellationToken = default)
    {
        FilterDefinition<BsonDocument> filter = Builders<BsonDocument>.Filter.Eq("_id", "journal:" + clientId);
        UpdateDefinition<BsonDocument> increment = Builders<BsonDocument>.Update.Inc("seq", 1L);
        FindOneAndUpdateOptions<BsonDocument> options = new()
        {
            IsUpsert = true,
            ReturnDocument = ReturnDocument.After,
        };

        BsonDocument counter = await _counters.FindOneAndUpdateAsync(session, filter, increment, options, cancellationToken);
        return counter["seq"].ToInt64();
    }
}
