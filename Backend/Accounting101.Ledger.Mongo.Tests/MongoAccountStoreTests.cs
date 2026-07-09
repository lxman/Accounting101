using Accounting101.Ledger.Core.Accounts;
using Accounting101.Ledger.Core.Journal;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Accounting101.Ledger.Mongo.Tests;

public sealed class MongoAccountStoreTests(MongoFixture fixture) : IClassFixture<MongoFixture>
{
    private MongoAccountStore NewStore() => new(fixture.Database, "accounts_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Account_round_trips_with_all_fields()
    {
        MongoAccountStore store = NewStore();
        var client = Guid.NewGuid();

        Account receivable = new()
        {
            Id = Guid.NewGuid(),
            ClientId = client,
            Number = "1200",
            Name = "Accounts Receivable",
            Type = AccountType.Asset,
            RequiredDimensions = ["Customer"],
        };

        await store.UpsertAsync(receivable);
        Account? read = await store.GetAsync(receivable.Id);

        Assert.NotNull(read);
        Assert.Equal("1200", read!.Number);
        Assert.Equal("Accounts Receivable", read.Name);
        Assert.Equal(AccountType.Asset, read.Type);
        Assert.Equal("Customer", read.RequiredDimension);
        Assert.True(read.Postable);
        Assert.Equal(Direction.Debit, read.NormalSide); // derived semantics survive the round-trip
    }

    [Fact]
    public async Task GetChart_builds_the_validated_client_chart()
    {
        MongoAccountStore store = NewStore();
        var client = Guid.NewGuid();

        Account assets = new() { Id = Guid.NewGuid(), ClientId = client, Number = "1000", Name = "Assets", Type = AccountType.Asset, Postable = false };
        Account cash = new() { Id = Guid.NewGuid(), ClientId = client, Number = "1100", Name = "Cash", Type = AccountType.Asset, ParentId = assets.Id };

        await store.UpsertAsync(assets);
        await store.UpsertAsync(cash);

        ChartOfAccounts chart = await store.GetChartAsync(client);

        Assert.Equal(2, chart.Accounts.Count);
        Assert.Equal(cash.Id, chart.FindByNumber("1100")!.Id);
        Assert.False(chart.IsLeaf(assets.Id));
        Assert.True(chart.IsLeaf(cash.Id));
    }

    [Fact]
    public async Task Upsert_updates_an_existing_account()
    {
        MongoAccountStore store = NewStore();
        var client = Guid.NewGuid();
        var id = Guid.NewGuid();

        await store.UpsertAsync(new Account { Id = id, ClientId = client, Number = "5000", Name = "Rent", Type = AccountType.Expense });
        await store.UpsertAsync(new Account { Id = id, ClientId = client, Number = "5000", Name = "Rent Expense", Type = AccountType.Expense });

        Account? read = await store.GetAsync(id);
        Assert.Equal("Rent Expense", read!.Name); // renamed, not duplicated
    }

    /// <summary>A document written before the RequiredDimension → RequiredDimensions rename carries the old
    /// single-value element and no RequiredDimensions array. It must still read cleanly, and its single
    /// dimension requirement must survive into the new set-shaped property.</summary>
    [Fact]
    public async Task Legacy_doc_with_old_RequiredDimension_field_reads_and_migrates_the_value()
    {
        string collectionName = "accounts_" + Guid.NewGuid().ToString("N");
        MongoAccountStore store = new(fixture.Database, collectionName);
        var client = Guid.NewGuid();
        var id = Guid.NewGuid();

        IMongoCollection<BsonDocument> raw = fixture.Database.GetCollection<BsonDocument>(collectionName);
        BsonDocument legacyDoc = new()
        {
            { "_id", new BsonBinaryData(id, GuidRepresentation.Standard) },
            { "ClientId", new BsonBinaryData(client, GuidRepresentation.Standard) },
            { "Number", "1200" },
            { "Name", "Accounts Receivable" },
            { "Type", "Asset" },
            { "Postable", true },
            { "RequiredDimension", "Customer" }, // OLD shape: single value, no RequiredDimensions array
            { "IsRetainedEarnings", false },
            { "Active", true },
        };
        await raw.InsertOneAsync(legacyDoc);

        Account? read = await store.GetAsync(id);

        Assert.NotNull(read);
        Assert.Contains("Customer", read!.RequiredDimensions);
    }

    /// <summary>A document carrying a field unknown to the current shape (e.g. a future rename) must not
    /// blow up the whole chart read — mirrors the sibling Control docs, which are already tolerant.</summary>
    [Fact]
    public async Task Doc_with_unknown_extra_field_does_not_throw_on_chart_read()
    {
        string collectionName = "accounts_" + Guid.NewGuid().ToString("N");
        MongoAccountStore store = new(fixture.Database, collectionName);
        var client = Guid.NewGuid();
        var id = Guid.NewGuid();

        IMongoCollection<BsonDocument> raw = fixture.Database.GetCollection<BsonDocument>(collectionName);
        BsonDocument doc = new()
        {
            { "_id", new BsonBinaryData(id, GuidRepresentation.Standard) },
            { "ClientId", new BsonBinaryData(client, GuidRepresentation.Standard) },
            { "Number", "5000" },
            { "Name", "Rent" },
            { "Type", "Expense" },
            { "Postable", true },
            { "RequiredDimensions", new BsonArray() },
            { "IsRetainedEarnings", false },
            { "Active", true },
            { "SomeFutureField", "whatever" },
        };
        await raw.InsertOneAsync(doc);

        ChartOfAccounts chart = await store.GetChartAsync(client);

        Assert.Single(chart.Accounts);
        Assert.Equal("Rent", chart.Accounts.Single().Name);
    }
}
