using Accounting101.Ledger.Mongo.Documents;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Accounting101.Ledger.Mongo.Tests;

public sealed class ModuleDocumentQueryTests(MongoFixture fixture) : IClassFixture<MongoFixture>
{
    private MongoDocumentStore Store() => new(fixture.Database);
    private static string Coll() => "invoicing_invoices_" + Guid.NewGuid().ToString("N");

    private static ModuleDocument Doc(string customer, string status, DocumentState state = DocumentState.Finalized) => new()
    {
        Id = Guid.NewGuid(),
        Tags = new Dictionary<string, string> { ["Customer"] = customer, ["Status"] = status },
        Body = new BsonDocument { ["n"] = 1 },
        State = state,
        Version = 1,
    };

    [Fact]
    public async Task Query_matches_on_tag_equality()
    {
        MongoDocumentStore store = Store();
        string c = Coll();
        string cust = Guid.NewGuid().ToString();
        await store.PutAsync(c, Doc(cust, "Finalized"));
        await store.PutAsync(c, Doc(cust, "Finalized"));
        await store.PutAsync(c, Doc(Guid.NewGuid().ToString(), "Finalized")); // other customer

        IReadOnlyList<ModuleDocument> hits = await store.QueryAsync(c, new Dictionary<string, string> { ["Customer"] = cust });
        Assert.Equal(2, hits.Count);
    }

    [Fact]
    public async Task Query_hides_superseded_and_voided_and_inactive_by_default()
    {
        MongoDocumentStore store = Store();
        string c = Coll();
        string cust = Guid.NewGuid().ToString();
        await store.PutAsync(c, Doc(cust, "Finalized"));
        await store.PutAsync(c, Doc(cust, "x", DocumentState.Superseded));
        await store.PutAsync(c, Doc(cust, "x", DocumentState.Voided));
        await store.PutAsync(c, Doc(cust, "x", DocumentState.Inactive));

        IReadOnlyList<ModuleDocument> hits = await store.QueryAsync(c, new Dictionary<string, string> { ["Customer"] = cust });
        Assert.Single(hits);
        Assert.Equal(DocumentState.Finalized, hits[0].State);
    }

    [Fact]
    public async Task EnsureTagIndexes_creates_the_declared_indexes()
    {
        MongoDocumentStore store = Store();
        string c = Coll();
        await store.PutAsync(c, Doc(Guid.NewGuid().ToString(), "Finalized")); // create the collection
        await store.EnsureTagIndexesAsync(c, ["Customer", "Status"]);

        var indexesCursor = await fixture.Database.GetCollection<ModuleDocument>(c).Indexes.ListAsync();
        List<BsonDocument> indexes = await indexesCursor.ToListAsync();
        Assert.Contains(indexes, ix => ix["name"].AsString == "tag_Customer");
        Assert.Contains(indexes, ix => ix["name"].AsString == "tag_Status");
    }
}
