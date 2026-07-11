using Accounting101.TestSupport;
using EphemeralMongo;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Accounting101.Ledger.Mongo.Tests;

public sealed class JournalStoreIndexTests
{
    [Fact]
    public async Task EnsureIndexes_creates_the_four_key_covering_index_and_drops_the_old_three_key()
    {
        IMongoRunner runner = await SharedMongo.InstanceAsync();
        IMongoDatabase db = new MongoClient(runner.ConnectionString).GetDatabase($"idx_{Guid.NewGuid():N}");
        var store = new MongoJournalStore(db);

        await store.EnsureIndexesAsync();

        List<BsonDocument> indexes = await (await db.GetCollection<BsonDocument>("journal").Indexes.ListAsync()).ToListAsync();
        List<string> names = indexes.Select(i => i["name"].AsString).ToList();

        Assert.Contains("client_status_posting_effdate", names);
        Assert.DoesNotContain("client_status_posting", names);

        BsonDocument covering = indexes.Single(i => i["name"].AsString == "client_status_posting_effdate");
        BsonDocument key = covering["key"].AsBsonDocument;
        Assert.Equal(new[] { "ClientId", "Status", "Posting", "EffectiveDate" }, key.Names.ToArray());
    }
}
