using Accounting101.Ledger.Mongo.Documents;
using MongoDB.Bson;

namespace Accounting101.Ledger.Mongo.Tests;

/// <summary>
/// Tests the shared paging layer added to MongoDocumentStore: optional skip/limit/descending/includeVoided params
/// on QueryAsync, and the new CountAsync. Mirrors the fixture setup in ModuleDocumentQueryTests.
/// </summary>
public sealed class DocumentStorePagingTests(MongoFixture fixture) : IClassFixture<MongoFixture>
{
    private MongoDocumentStore Store() => new(fixture.Database);
    private static string Coll() => "paging_test_" + Guid.NewGuid().ToString("N");

    /// <summary>Builds a finalized doc with a deterministic Sequence so sort order is predictable.</summary>
    private static ModuleDocument Doc(long sequence) => new()
    {
        Id = Guid.NewGuid(),
        Tags = new Dictionary<string, string> { ["Type"] = "Invoice" },
        Body = new BsonDocument { ["seq"] = sequence },
        State = DocumentState.Finalized,
        Sequence = sequence,
        Version = 1,
    };

    private static ModuleDocument VoidedDoc(long sequence) => new()
    {
        Id = Guid.NewGuid(),
        Tags = new Dictionary<string, string> { ["Type"] = "Invoice" },
        Body = new BsonDocument { ["seq"] = sequence },
        State = DocumentState.Voided,
        Sequence = sequence,
        Version = 2,
    };

    /// <summary>
    /// Default (unpaged) call — back-compat: returns all non-voided docs, no sort guarantee, voided hidden.
    /// </summary>
    [Fact]
    public async Task Default_query_returns_all_non_voided_back_compat()
    {
        MongoDocumentStore store = Store();
        string c = Coll();

        for (long i = 1; i <= 5; i++) await store.PutAsync(c, Doc(i));
        await store.PutAsync(c, VoidedDoc(99));

        IReadOnlyList<ModuleDocument> hits = await store.QueryAsync(c, new Dictionary<string, string> { ["Type"] = "Invoice" });

        Assert.Equal(5, hits.Count);
        Assert.All(hits, d => Assert.NotEqual(DocumentState.Voided, d.State));
    }

    /// <summary>Descending page 1 (skip=0, limit=2) returns the 2 highest-Sequence docs.</summary>
    [Fact]
    public async Task Paged_descending_first_page_returns_highest_sequence_docs()
    {
        MongoDocumentStore store = Store();
        string c = Coll();

        for (long i = 1; i <= 5; i++) await store.PutAsync(c, Doc(i));

        IReadOnlyList<ModuleDocument> hits = await store.QueryAsync(c,
            new Dictionary<string, string> { ["Type"] = "Invoice" },
            skip: 0, limit: 2, descending: true);

        Assert.Equal(2, hits.Count);
        Assert.Equal(5L, hits[0].Sequence);
        Assert.Equal(4L, hits[1].Sequence);
    }

    /// <summary>Descending page 2 (skip=2, limit=2) returns the next 2 docs.</summary>
    [Fact]
    public async Task Paged_descending_second_page_returns_next_docs()
    {
        MongoDocumentStore store = Store();
        string c = Coll();

        for (long i = 1; i <= 5; i++) await store.PutAsync(c, Doc(i));

        IReadOnlyList<ModuleDocument> hits = await store.QueryAsync(c,
            new Dictionary<string, string> { ["Type"] = "Invoice" },
            skip: 2, limit: 2, descending: true);

        Assert.Equal(2, hits.Count);
        Assert.Equal(3L, hits[0].Sequence);
        Assert.Equal(2L, hits[1].Sequence);
    }

    /// <summary>Ascending order (descending=false) returns lowest-Sequence docs first.</summary>
    [Fact]
    public async Task Paged_ascending_first_page_returns_lowest_sequence_docs()
    {
        MongoDocumentStore store = Store();
        string c = Coll();

        for (long i = 1; i <= 5; i++) await store.PutAsync(c, Doc(i));

        IReadOnlyList<ModuleDocument> hits = await store.QueryAsync(c,
            new Dictionary<string, string> { ["Type"] = "Invoice" },
            skip: 0, limit: 2, descending: false);

        Assert.Equal(2, hits.Count);
        Assert.Equal(1L, hits[0].Sequence);
        Assert.Equal(2L, hits[1].Sequence);
    }

    /// <summary>includeVoided=true (without limit) includes the voided doc.</summary>
    [Fact]
    public async Task Query_with_includeVoided_true_returns_voided_docs()
    {
        MongoDocumentStore store = Store();
        string c = Coll();

        for (long i = 1; i <= 5; i++) await store.PutAsync(c, Doc(i));
        await store.PutAsync(c, VoidedDoc(99));

        IReadOnlyList<ModuleDocument> hits = await store.QueryAsync(c,
            new Dictionary<string, string> { ["Type"] = "Invoice" },
            includeVoided: true);

        Assert.Equal(6, hits.Count);
        Assert.Contains(hits, d => d.State == DocumentState.Voided);
    }

    /// <summary>CountAsync returns the non-voided count by default.</summary>
    [Fact]
    public async Task CountAsync_returns_non_voided_count_by_default()
    {
        MongoDocumentStore store = Store();
        string c = Coll();

        for (long i = 1; i <= 5; i++) await store.PutAsync(c, Doc(i));
        await store.PutAsync(c, VoidedDoc(99));

        long count = await store.CountAsync(c, new Dictionary<string, string> { ["Type"] = "Invoice" });

        Assert.Equal(5, count);
    }

    /// <summary>CountAsync with includeVoided=true returns the full count including voided.</summary>
    [Fact]
    public async Task CountAsync_with_includeVoided_true_counts_all_docs()
    {
        MongoDocumentStore store = Store();
        string c = Coll();

        for (long i = 1; i <= 5; i++) await store.PutAsync(c, Doc(i));
        await store.PutAsync(c, VoidedDoc(99));

        long count = await store.CountAsync(c, new Dictionary<string, string> { ["Type"] = "Invoice" },
            includeVoided: true);

        Assert.Equal(6, count);
    }
}
