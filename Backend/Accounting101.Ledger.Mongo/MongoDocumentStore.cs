using Accounting101.Ledger.Mongo.Documents;
using Accounting101.Ledger.Mongo.Serialization;
using MongoDB.Driver;

namespace Accounting101.Ledger.Mongo;

/// <summary>
/// Raw persistence for module documents in a per-client database. Pure storage — no authorization and
/// no audit (the Api-layer <c>ScopedDocumentStore</c> adds those). Collections are passed by their full
/// physical name (the caller has already imposed the module's namespace prefix). Audited callers pass a
/// session so the document write commits together with the audit append.
/// </summary>
public sealed class MongoDocumentStore
{
    private readonly IMongoDatabase _database;

    static MongoDocumentStore() => LedgerMongoBootstrap.RegisterOnce();

    public MongoDocumentStore(IMongoDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    private static readonly DocumentState[] HiddenStates =
        [DocumentState.Inactive, DocumentState.Superseded, DocumentState.Voided];

    private IMongoCollection<ModuleDocument> Collection(string collection) =>
        _database.GetCollection<ModuleDocument>(collection);

    public async Task<ModuleDocument?> GetAsync(
        string collection, Guid id, IClientSessionHandle? session = null, CancellationToken cancellationToken = default)
    {
        IMongoCollection<ModuleDocument> c = Collection(collection);
        return session is null
            ? await c.Find(d => d.Id == id).FirstOrDefaultAsync(cancellationToken)
            : await c.Find(session, d => d.Id == id).FirstOrDefaultAsync(cancellationToken);
    }

    public Task PutAsync(
        string collection, ModuleDocument document, IClientSessionHandle? session = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        IMongoCollection<ModuleDocument> c = Collection(collection);
        FilterDefinition<ModuleDocument> filter = Builders<ModuleDocument>.Filter.Eq(d => d.Id, document.Id);
        ReplaceOptions options = new() { IsUpsert = true };
        return session is null
            ? c.ReplaceOneAsync(filter, document, options, cancellationToken)
            : c.ReplaceOneAsync(session, filter, document, options, cancellationToken);
    }

    public Task DeleteAsync(
        string collection, Guid id, IClientSessionHandle? session = null, CancellationToken cancellationToken = default)
    {
        IMongoCollection<ModuleDocument> c = Collection(collection);
        FilterDefinition<ModuleDocument> filter = Builders<ModuleDocument>.Filter.Eq(d => d.Id, id);
        return session is null
            ? c.DeleteOneAsync(filter, cancellationToken)
            : c.DeleteOneAsync(session, filter, null, cancellationToken);
    }

    /// <summary>
    /// Documents whose tags match every entry in <paramref name="tagFilter"/> (equality). Hides
    /// Inactive/Superseded/Voided by default — the active view, mirroring the journal's "replay sums Active" default.
    /// When <paramref name="limit"/> is supplied, sorts by Sequence and pages; otherwise returns all (unbounded, no sort).
    /// </summary>
    public async Task<IReadOnlyList<ModuleDocument>> QueryAsync(
        string collection, IReadOnlyDictionary<string, string> tagFilter,
        int? skip = null, int? limit = null, bool descending = true, bool includeVoided = false,
        CancellationToken cancellationToken = default)
    {
        IFindFluent<ModuleDocument, ModuleDocument> find = Collection(collection).Find(MatchFilter(tagFilter, includeVoided));
        if (limit is not null)
        {
            SortDefinition<ModuleDocument> sort = descending
                ? Builders<ModuleDocument>.Sort.Descending(d => d.Sequence)
                : Builders<ModuleDocument>.Sort.Ascending(d => d.Sequence);
            find = find.Sort(sort).Skip(skip > 0 ? skip : 0).Limit(Math.Clamp(limit.Value, 1, 200));
        }
        return await find.ToListAsync(cancellationToken);
    }

    public Task<long> CountAsync(
        string collection, IReadOnlyDictionary<string, string> tagFilter, bool includeVoided = false,
        CancellationToken cancellationToken = default) =>
        Collection(collection).CountDocumentsAsync(MatchFilter(tagFilter, includeVoided), cancellationToken: cancellationToken);

    private static FilterDefinition<ModuleDocument> MatchFilter(IReadOnlyDictionary<string, string> tagFilter, bool includeVoided)
    {
        FilterDefinitionBuilder<ModuleDocument> b = Builders<ModuleDocument>.Filter;
        List<FilterDefinition<ModuleDocument>> clauses = [];
        if (!includeVoided) clauses.Add(b.Nin(d => d.State, HiddenStates));   // default: hide Inactive/Superseded/Voided
        clauses.AddRange(tagFilter.Select(t => b.Eq("Tags." + t.Key, t.Value)));
        return clauses.Count > 0 ? b.And(clauses) : b.Empty;
    }

    /// <summary>Ensure an index on each declared indexed-tag key (<c>Tags.{key}</c>). Idempotent.</summary>
    public async Task EnsureTagIndexesAsync(
        string collection, IReadOnlyList<string> indexedTags, CancellationToken cancellationToken = default)
    {
        if (indexedTags.Count == 0)
            return;

        List<CreateIndexModel<ModuleDocument>> models = indexedTags
            .Select(tag => new CreateIndexModel<ModuleDocument>(
                Builders<ModuleDocument>.IndexKeys.Ascending("Tags." + tag),
                new CreateIndexOptions { Name = "tag_" + tag }))
            .ToList();

        await Collection(collection).Indexes.CreateManyAsync(models, cancellationToken);
    }
}
