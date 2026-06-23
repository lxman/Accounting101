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
}
