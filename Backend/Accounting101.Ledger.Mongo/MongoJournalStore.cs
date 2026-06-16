using Accounting101.Ledger.Core.Journal;
using MongoDB.Driver;

namespace Accounting101.Ledger.Mongo;

/// <summary>
/// Append-only journal persistence: one document per entry, write-through inserts.
/// Reads rehydrate through <see cref="JournalEntry.Create"/>, so a load re-checks
/// the balance invariant. The supersede/void lifecycle lands with the lifecycle
/// increment (and needs a replica set for its two-write transaction).
/// </summary>
public sealed class MongoJournalStore
{
    private readonly IMongoCollection<JournalEntryDocument> _entries;

    static MongoJournalStore() => LedgerMongoBootstrap.RegisterOnce();

    public MongoJournalStore(IMongoDatabase database, string collectionName = "journal")
    {
        ArgumentNullException.ThrowIfNull(database);
        _entries = database.GetCollection<JournalEntryDocument>(collectionName);
    }

    /// <summary>Durably append one entry. The entry id is the document <c>_id</c>.</summary>
    public Task AppendAsync(JournalEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return _entries.InsertOneAsync(JournalEntryDocument.FromDomain(entry), cancellationToken: cancellationToken);
    }

    public async Task<JournalEntry?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        JournalEntryDocument? doc = await _entries
            .Find(e => e.Id == id)
            .FirstOrDefaultAsync(cancellationToken);

        return doc?.ToDomain();
    }

    public async Task<IReadOnlyList<JournalEntry>> GetByClientAsync(
        Guid clientId, CancellationToken cancellationToken = default)
    {
        List<JournalEntryDocument> docs = await _entries
            .Find(e => e.ClientId == clientId)
            .ToListAsync(cancellationToken);

        return docs.Select(d => d.ToDomain()).ToList();
    }

    /// <summary>Every entry with a line touching <paramref name="accountId"/> (served by the multikey index).</summary>
    public async Task<IReadOnlyList<JournalEntry>> GetTouchingAccountAsync(
        Guid clientId, Guid accountId, CancellationToken cancellationToken = default)
    {
        FilterDefinitionBuilder<JournalEntryDocument> f = Builders<JournalEntryDocument>.Filter;
        FilterDefinition<JournalEntryDocument> filter = f.And(
            f.Eq(e => e.ClientId, clientId),
            f.ElemMatch(e => e.Lines, l => l.AccountId == accountId));

        List<JournalEntryDocument> docs = await _entries.Find(filter).ToListAsync(cancellationToken);
        return docs.Select(d => d.ToDomain()).ToList();
    }

    /// <summary>Creates the indexes the prototype's read paths rely on. Idempotent.</summary>
    public Task EnsureIndexesAsync(CancellationToken cancellationToken = default)
    {
        IndexKeysDefinitionBuilder<JournalEntryDocument> keys = Builders<JournalEntryDocument>.IndexKeys;

        CreateIndexModel<JournalEntryDocument>[] models =
        [
            new(keys.Ascending(e => e.ClientId).Ascending(e => e.Status).Ascending(e => e.Posting),
                new CreateIndexOptions { Name = "client_status_posting" }),
            new(keys.Ascending(e => e.ClientId).Ascending("Lines.AccountId"),
                new CreateIndexOptions { Name = "client_lineAccount" }),
            new(keys.Ascending(e => e.ClientId).Ascending(e => e.EffectiveDate),
                new CreateIndexOptions { Name = "client_effectiveDate" }),
            new(keys.Ascending(e => e.ClientId).Ascending(e => e.SequenceNumber),
                new CreateIndexOptions { Name = "client_sequence_unique", Unique = true }),
        ];

        return _entries.Indexes.CreateManyAsync(models, cancellationToken);
    }
}
