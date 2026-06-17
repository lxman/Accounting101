using System.Globalization;
using Accounting101.Ledger.Core.Journal;
using Accounting101.Ledger.Mongo.Documents;
using Accounting101.Ledger.Mongo.Serialization;
using MongoDB.Bson;
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

    /// <summary>
    /// Persist a lifecycle/posting-state transition of an existing entry (approve, void,
    /// supersede). Only the status fields change — the lines/references are unchanged, so
    /// the "a reference is never erased" rule holds.
    /// </summary>
    public Task ReplaceAsync(JournalEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return _entries.ReplaceOneAsync(
            e => e.Id == entry.Id,
            JournalEntryDocument.FromDomain(entry),
            new ReplaceOptions(),
            cancellationToken);
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

    /// <summary>
    /// Server-side trial balance: folds the journal in MongoDB via aggregation,
    /// applying the same on-the-books gate as <see cref="LedgerReplay"/> (Active +
    /// Posted) and the same debit-positive signed-effect (Debit => +Amount,
    /// Credit => -Amount). The "load entries and fold in C#" path is
    /// <see cref="GetByClientAsync"/> + <see cref="LedgerReplay.Balances"/>.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, decimal>> AggregateBalancesAsync(
        Guid clientId, DateOnly? asOf = null, CancellationToken cancellationToken = default)
    {
        BsonDocument match = new()
        {
            { "ClientId", new BsonBinaryData(clientId, GuidRepresentation.Standard) },
            { "Status", nameof(LifecycleStatus.Active) },
            { "Posting", nameof(PostingState.Posted) },
        };
        if (asOf is { } asOfDate)
            match.Add("EffectiveDate", new BsonDocument("$lte", asOfDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));

        BsonDocument[] pipeline =
        [
            new("$match", match),
            new("$unwind", "$Lines"),
            new("$group", new BsonDocument
            {
                { "_id", "$Lines.AccountId" },
                {
                    "balance", new BsonDocument("$sum", new BsonDocument("$cond", new BsonArray
                    {
                        new BsonDocument("$eq", new BsonArray { "$Lines.Direction", nameof(Direction.Debit) }),
                        "$Lines.Amount",
                        new BsonDocument("$multiply", new BsonArray { "$Lines.Amount", -1 }),
                    }))
                },
            }),
        ];

        IAsyncCursor<BsonDocument> cursor =
            await _entries.AggregateAsync<BsonDocument>(pipeline, cancellationToken: cancellationToken);
        List<BsonDocument> results = await cursor.ToListAsync(cancellationToken);

        Dictionary<Guid, decimal> balances = new(results.Count);
        foreach (BsonDocument doc in results)
        {
            var accountId = doc["_id"].AsBsonBinaryData.ToGuid(GuidRepresentation.Standard);
            balances[accountId] = doc["balance"].AsDecimal;
        }

        return balances;
    }
}
