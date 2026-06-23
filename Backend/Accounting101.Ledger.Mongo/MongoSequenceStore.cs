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
    /// Allocate the next value of a named counter (atomic <c>findAndModify</c> <c>$inc</c>, upsert). The
    /// first allocation returns 1. Joins the caller's transaction when a session is supplied, so the number
    /// commits with the work that consumed it (keeping the sequence gapless).
    /// </summary>
    public async Task<long> NextAsync(
        string counterId, IClientSessionHandle? session = null, CancellationToken cancellationToken = default)
    {
        FilterDefinition<BsonDocument> filter = Builders<BsonDocument>.Filter.Eq("_id", counterId);
        UpdateDefinition<BsonDocument> increment = Builders<BsonDocument>.Update.Inc("seq", 1L);
        FindOneAndUpdateOptions<BsonDocument> options = new()
        {
            IsUpsert = true,
            ReturnDocument = ReturnDocument.After,
        };

        BsonDocument counter = session is null
            ? await _counters.FindOneAndUpdateAsync(filter, increment, options, cancellationToken)
            : await _counters.FindOneAndUpdateAsync(session, filter, increment, options, cancellationToken);
        return counter["seq"].ToInt64();
    }

    /// <summary>
    /// Allocate the next journal sequence number for a client. Joins the caller's transaction, so the
    /// increment commits together with the entry insert — if the post rolls back, so does the number,
    /// keeping the per-client sequence gapless. The first allocation returns 1.
    /// </summary>
    public Task<long> NextJournalAsync(
        Guid clientId, IClientSessionHandle session, CancellationToken cancellationToken = default) =>
        NextAsync("journal:" + clientId, session, cancellationToken);

    /// <summary>
    /// Raise the journal counter to at least <paramref name="atLeast"/> (never lowers it). Called when an
    /// entry is appended with an explicit, caller-chosen number (bulk import), so the counter advances past
    /// the imported block and a later auto-allocation cannot collide with it. Joins the caller's transaction.
    /// </summary>
    public Task ReseedJournalAsync(
        Guid clientId, long atLeast, IClientSessionHandle session, CancellationToken cancellationToken = default)
    {
        FilterDefinition<BsonDocument> filter = Builders<BsonDocument>.Filter.Eq("_id", "journal:" + clientId);
        UpdateDefinition<BsonDocument> raise = Builders<BsonDocument>.Update.Max("seq", atLeast);
        return _counters.UpdateOneAsync(session, filter, raise, new UpdateOptions { IsUpsert = true }, cancellationToken);
    }
}
