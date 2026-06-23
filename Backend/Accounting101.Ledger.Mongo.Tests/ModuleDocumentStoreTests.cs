using Accounting101.Ledger.Mongo.Documents;
using MongoDB.Bson;

namespace Accounting101.Ledger.Mongo.Tests;

public sealed class ModuleDocumentStoreTests(MongoFixture fixture) : IClassFixture<MongoFixture>
{
    private MongoDocumentStore Store() => new(fixture.Database);
    private static string Coll() => "invoicing_customers_" + Guid.NewGuid().ToString("N");

    private static ModuleDocument Doc(Guid id, string name) => new()
    {
        Id = id,
        Tags = new Dictionary<string, string> { ["Status"] = "Active" },
        Body = new BsonDocument { ["name"] = name },
        State = DocumentState.Active,
        Version = 1,
    };

    [Fact]
    public async Task Put_then_Get_round_trips_the_opaque_body_and_tags()
    {
        MongoDocumentStore store = Store();
        string c = Coll();
        Guid id = Guid.NewGuid();

        await store.PutAsync(c, Doc(id, "Acme"));
        ModuleDocument? read = await store.GetAsync(c, id);

        Assert.NotNull(read);
        Assert.Equal("Acme", read!.Body["name"].AsString);
        Assert.Equal("Active", read.Tags["Status"]);
        Assert.Equal(DocumentState.Active, read.State);
    }

    [Fact]
    public async Task Put_replaces_an_existing_document_by_id()
    {
        MongoDocumentStore store = Store();
        string c = Coll();
        Guid id = Guid.NewGuid();

        await store.PutAsync(c, Doc(id, "Acme"));
        await store.PutAsync(c, Doc(id, "Acme Renamed"));

        ModuleDocument? read = await store.GetAsync(c, id);
        Assert.Equal("Acme Renamed", read!.Body["name"].AsString);
    }

    [Fact]
    public async Task Delete_removes_the_document()
    {
        MongoDocumentStore store = Store();
        string c = Coll();
        Guid id = Guid.NewGuid();

        await store.PutAsync(c, Doc(id, "Acme"));
        await store.DeleteAsync(c, id);

        Assert.Null(await store.GetAsync(c, id));
    }
}
