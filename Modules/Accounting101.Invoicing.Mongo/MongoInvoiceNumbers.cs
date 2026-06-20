using MongoDB.Bson;
using MongoDB.Driver;

namespace Accounting101.Invoicing.Mongo;

/// <summary>
/// The module's invoice-number sequence — its own atomic counter in the client's database, the same
/// findAndModify-$inc pattern the engine uses for the journal sequence. Distinct counter, distinct
/// collection: the module owns its document numbering, so it never collides with the journal's.
/// </summary>
public sealed class MongoInvoiceNumbers(IInvoicingDatabaseResolver databases) : IInvoiceNumbers
{
    private const string CollectionName = "invoicing_counters";

    static MongoInvoiceNumbers() => InvoicingMongoBootstrap.RegisterOnce();

    public async Task<string> NextAsync(Guid clientId, CancellationToken cancellationToken = default)
    {
        IMongoDatabase database = await databases.ResolveAsync(clientId, cancellationToken);
        IMongoCollection<BsonDocument> counters = database.GetCollection<BsonDocument>(CollectionName);

        // Keyed by client even though each client already has its own database — same belt-and-suspenders
        // keying the engine uses for the journal counter.
        FilterDefinition<BsonDocument> filter = Builders<BsonDocument>.Filter.Eq("_id", "invoice:" + clientId);
        UpdateDefinition<BsonDocument> increment = Builders<BsonDocument>.Update.Inc("seq", 1L);
        FindOneAndUpdateOptions<BsonDocument> options = new()
        {
            IsUpsert = true,
            ReturnDocument = ReturnDocument.After,
        };

        BsonDocument counter = await counters.FindOneAndUpdateAsync(filter, increment, options, cancellationToken);
        return $"INV-{counter["seq"].ToInt64():D5}";
    }
}
