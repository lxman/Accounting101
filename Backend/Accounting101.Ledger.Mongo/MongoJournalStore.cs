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

    /// <summary>
    /// Durably append one entry (the id is the document <c>_id</c>). Joins the given transaction
    /// session when one is supplied, so the append commits atomically with its audit record.
    /// </summary>
    public Task AppendAsync(JournalEntry entry, IClientSessionHandle? session = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        JournalEntryDocument doc = JournalEntryDocument.FromDomain(entry);
        return session is null
            ? _entries.InsertOneAsync(doc, cancellationToken: cancellationToken)
            : _entries.InsertOneAsync(session, doc, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Persist a lifecycle/posting-state transition of an existing entry (approve, void, supersede)
    /// with optimistic concurrency. Only the status fields change — the lines/references are unchanged,
    /// so the "a reference is never erased" rule holds. The replace only matches if the stored version
    /// is the one this transition was derived from (<c>entry.Version - 1</c>); otherwise a concurrent
    /// writer moved the entry first and <see cref="ConcurrencyConflictException"/> is thrown, before any
    /// projection update, so an interleaved transition can never be double-applied.
    /// </summary>
    public async Task ReplaceAsync(JournalEntry entry, IClientSessionHandle? session = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        int expectedVersion = entry.Version - 1;
        FilterDefinition<JournalEntryDocument> filter = Builders<JournalEntryDocument>.Filter
            .Where(e => e.Id == entry.Id && e.Version == expectedVersion);
        JournalEntryDocument doc = JournalEntryDocument.FromDomain(entry);

        ReplaceOneResult result = session is null
            ? await _entries.ReplaceOneAsync(filter, doc, new ReplaceOptions(), cancellationToken)
            : await _entries.ReplaceOneAsync(session, filter, doc, new ReplaceOptions(), cancellationToken);

        if (result.MatchedCount == 0)
            throw new ConcurrencyConflictException(entry.Id, expectedVersion);
    }

    public async Task<JournalEntry?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        JournalEntryDocument? doc = await _entries
            .Find(e => e.Id == id)
            .FirstOrDefaultAsync(cancellationToken);

        return doc?.ToDomain();
    }

    /// <summary>
    /// The client's entries in sequence order. <paramref name="skip"/>/<paramref name="limit"/> page the
    /// result (limit &lt;= 0 means no limit, for internal full scans like a projection rebuild); the list
    /// endpoint applies a default cap so a whole-journal scan can't be requested by accident.
    /// </summary>
    public async Task<IReadOnlyList<JournalEntry>> GetByClientAsync(
        Guid clientId, int skip = 0, int limit = 0, CancellationToken cancellationToken = default)
    {
        List<JournalEntryDocument> docs = await _entries
            .Find(e => e.ClientId == clientId)
            .SortBy(e => e.SequenceNumber)
            .Skip(skip > 0 ? skip : null)
            .Limit(limit > 0 ? limit : null)
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

    /// <summary>
    /// Every entry with a line carrying <paramref name="value"/> on the given subledger dimension
    /// <paramref name="type"/> — the subsidiary-ledger detail for one customer, vendor, employee, etc.
    /// (served by the dimension index).
    /// </summary>
    public async Task<IReadOnlyList<JournalEntry>> GetTouchingDimensionAsync(
        Guid clientId, string type, Guid value, CancellationToken cancellationToken = default)
    {
        FilterDefinition<LineDocument> lineFilter = Builders<LineDocument>.Filter
            .ElemMatch(l => l.Dimensions, d => d.Type == type && d.Value == value);

        FilterDefinitionBuilder<JournalEntryDocument> f = Builders<JournalEntryDocument>.Filter;
        FilterDefinition<JournalEntryDocument> filter = f.And(
            f.Eq(e => e.ClientId, clientId),
            f.ElemMatch(e => e.Lines, lineFilter));

        List<JournalEntryDocument> docs = await _entries.Find(filter).ToListAsync(cancellationToken);
        return docs.Select(d => d.ToDomain()).ToList();
    }

    /// <summary>
    /// Every entry spawned by one source document — the back-link an upstream module follows to reach
    /// the journal from one of its business documents (invoice, pay-run). More than one can share a
    /// <paramref name="sourceRef"/>: a revised entry leaves the superseded original behind, and a
    /// reversal points at the same source. Served by the (client, sourceRef) index.
    /// </summary>
    public async Task<IReadOnlyList<JournalEntry>> GetBySourceRefAsync(
        Guid clientId, Guid sourceRef, CancellationToken cancellationToken = default)
    {
        FilterDefinitionBuilder<JournalEntryDocument> f = Builders<JournalEntryDocument>.Filter;
        FilterDefinition<JournalEntryDocument> filter = f.And(
            f.Eq(e => e.ClientId, clientId),
            f.Eq(e => e.SourceRef, sourceRef));

        List<JournalEntryDocument> docs = await _entries.Find(filter).ToListAsync(cancellationToken);
        return docs.Select(d => d.ToDomain()).ToList();
    }

    /// <summary>
    /// Every entry that reverses <paramref name="originalId"/> (its <see cref="JournalEntry.ReversalOf"/>
    /// points here). Lets the reverse path detect — and refuse — a second reversal of the same entry, and
    /// lets a caller walk from an entry to the reversal that negated it without storing a back-pointer on
    /// the (possibly frozen) original.
    /// </summary>
    public async Task<IReadOnlyList<JournalEntry>> GetByReversalOfAsync(
        Guid clientId, Guid originalId, CancellationToken cancellationToken = default)
    {
        FilterDefinitionBuilder<JournalEntryDocument> f = Builders<JournalEntryDocument>.Filter;
        FilterDefinition<JournalEntryDocument> filter = f.And(
            f.Eq(e => e.ClientId, clientId),
            f.Eq(e => e.ReversalOf, originalId));

        List<JournalEntryDocument> docs = await _entries.Find(filter).ToListAsync(cancellationToken);
        return docs.Select(d => d.ToDomain()).ToList();
    }

    /// <summary>
    /// Every entry for <paramref name="clientId"/> with <see cref="JournalEntry.Reference"/> exactly equal
    /// to <paramref name="reference"/>, in sequence order. Returns an empty list when no entry carries that
    /// reference — callers may rely on the honest-empty guarantee. Must not return entries belonging to other
    /// clients even when they share the same reference string.
    /// </summary>
    public async Task<IReadOnlyList<JournalEntry>> GetByReferenceAsync(
        Guid clientId, string reference, CancellationToken cancellationToken = default)
    {
        FilterDefinitionBuilder<JournalEntryDocument> f = Builders<JournalEntryDocument>.Filter;
        FilterDefinition<JournalEntryDocument> filter = f.And(
            f.Eq(e => e.ClientId, clientId),
            f.Eq(e => e.Reference, reference));
        List<JournalEntryDocument> docs = await _entries
            .Find(filter)
            .SortBy(e => e.SequenceNumber)
            .ToListAsync(cancellationToken);
        return docs.Select(d => d.ToDomain()).ToList();
    }

    /// <summary>
    /// Entries for <paramref name="clientId"/> whose posting state matches <paramref name="posting"/>,
    /// paged via <paramref name="skip"/>/<paramref name="limit"/>, in sequence order. Served by the
    /// <c>client_status_posting</c> index. <paramref name="limit"/> &lt;= 0 means no limit.
    /// </summary>
    public async Task<IReadOnlyList<JournalEntry>> GetByPostingAsync(
        Guid clientId, PostingState posting, int skip, int limit,
        CancellationToken cancellationToken = default)
    {
        FilterDefinitionBuilder<JournalEntryDocument> f = Builders<JournalEntryDocument>.Filter;
        FilterDefinition<JournalEntryDocument> filter = f.And(
            f.Eq(e => e.ClientId, clientId),
            f.Eq("Posting", posting.ToString()));
        List<JournalEntryDocument> docs = await _entries
            .Find(filter)
            .SortBy(e => e.SequenceNumber)
            .Skip(skip > 0 ? skip : null)
            .Limit(limit > 0 ? limit : null)
            .ToListAsync(cancellationToken);
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
            new(keys.Ascending(e => e.ClientId).Ascending(e => e.SourceRef),
                new CreateIndexOptions { Name = "client_sourceRef" }),
            new(keys.Ascending(e => e.ClientId).Ascending("Lines.Dimensions.Type").Ascending("Lines.Dimensions.Value"),
                new CreateIndexOptions { Name = "client_lineDimension" }),
        ];

        return _entries.Indexes.CreateManyAsync(models, cancellationToken);
    }

    /// <summary>
    /// Active, PendingApproval entries dated on or before <paramref name="asOf"/> — exactly the entries a
    /// close through asOf would strand. Read through the session so the close's gate runs on the same
    /// transactional snapshot as its freeze write. Empty when the period is clean to close.
    /// </summary>
    public async Task<IReadOnlyList<JournalEntry>> GetPendingThroughAsync(
        Guid clientId, DateOnly asOf, IClientSessionHandle session, CancellationToken cancellationToken = default)
    {
        FilterDefinitionBuilder<JournalEntryDocument> f = Builders<JournalEntryDocument>.Filter;
        FilterDefinition<JournalEntryDocument> filter = f.And(
            f.Eq(e => e.ClientId, clientId),
            f.Eq("Status", nameof(LifecycleStatus.Active)),
            f.Eq("Posting", nameof(PostingState.PendingApproval)),
            f.Lte("EffectiveDate", Iso(asOf)));   // reuse the same date encoding AggregateBalances uses

        List<JournalEntryDocument> docs = await _entries.Find(session, filter).ToListAsync(cancellationToken);
        return docs.Select(d => d.ToDomain()).ToList();
    }

    /// <summary>
    /// Server-side trial balance: folds the journal in MongoDB via aggregation,
    /// applying the same on-the-books gate as <see cref="LedgerReplay"/> (Active +
    /// Posted) and the same debit-positive signed-effect (Debit => +Amount,
    /// Credit => -Amount). With <paramref name="asOf"/>, balances are taken at that
    /// instant (entries after it are excluded). The "load entries and fold in C#"
    /// path is <see cref="GetByClientAsync"/> + <see cref="LedgerReplay.Balances"/>.
    /// </summary>
    public Task<IReadOnlyDictionary<Guid, decimal>> AggregateBalancesAsync(
        Guid clientId, DateOnly? asOf = null, CancellationToken cancellationToken = default)
    {
        BsonDocument match = OnBooks(clientId);
        if (asOf is { } asOfDate)
            match.Add("EffectiveDate", new BsonDocument("$lte", Iso(asOfDate)));

        return FoldAsync(match, null, cancellationToken);
    }

    /// <summary>
    /// Session-aware overload of <see cref="AggregateBalancesAsync(Guid,DateOnly?,CancellationToken)"/> —
    /// reads the balance fold through <paramref name="session"/> so a period close can snapshot balances on
    /// the same transactional snapshot as its fence write.
    /// </summary>
    public Task<IReadOnlyDictionary<Guid, decimal>> AggregateBalancesAsync(
        Guid clientId, DateOnly? asOf, IClientSessionHandle session, CancellationToken cancellationToken = default)
    {
        BsonDocument match = OnBooks(clientId);
        if (asOf is { } asOfDate) match.Add("EffectiveDate", new BsonDocument("$lte", Iso(asOfDate)));
        return FoldAsync(match, session, cancellationToken);
    }

    /// <summary>
    /// Per-account activity over a window — the same debit-positive fold as
    /// <see cref="AggregateBalancesAsync"/>, restricted to entries whose effective date falls within
    /// [<paramref name="from"/>, <paramref name="to"/>]. Feeds the income statement and the cash-flow
    /// statement, both of which measure flow over a period rather than a balance at an instant.
    /// Year-end <see cref="EntryType.Closing"/> entries are excluded: they are cash-neutral mechanics
    /// that reset the temporaries into retained earnings, so counting them would zero out a period's
    /// revenue, expense, and net income whenever the window contains the close.
    /// </summary>
    public Task<IReadOnlyDictionary<Guid, decimal>> AggregateActivityAsync(
        Guid clientId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        BsonDocument match = OnBooks(clientId);
        match.Add("EffectiveDate", new BsonDocument { { "$gte", Iso(from) }, { "$lte", Iso(to) } });
        match.Add("Type", new BsonDocument("$ne", nameof(EntryType.Closing)));

        return FoldAsync(match, null, cancellationToken);
    }

    /// <summary>
    /// Subsidiary-ledger balances: the same on-the-books, debit-positive fold as
    /// <see cref="AggregateBalancesAsync"/>, but grouped one level finer — by (account, dimension value)
    /// instead of by account alone. With <paramref name="accountId"/> the fold is scoped to one control
    /// account (e.g. Accounts Receivable broken out per customer); without it, every account carrying the
    /// dimension is included. Because it is the very same lines grouped finer, the per-dimension balances
    /// sum back to the control-account balance by construction — the subledger cannot drift from the GL.
    /// Only lines that actually carry the dimension contribute.
    /// </summary>
    public async Task<IReadOnlyList<SubledgerBalance>> AggregateSubledgerAsync(
        Guid clientId, string dimensionType, Guid? accountId = null, DateOnly? asOf = null,
        CancellationToken cancellationToken = default)
    {
        BsonDocument match = OnBooks(clientId);
        if (asOf is { } asOfDate)
            match.Add("EffectiveDate", new BsonDocument("$lte", Iso(asOfDate)));

        // After unwinding lines and their dimension tags, keep only the tags of the requested type (a
        // line carries at most one tag per type, so its amount is counted once), optionally on one account.
        BsonDocument tagMatch = new() { { "Lines.Dimensions.Type", dimensionType } };
        if (accountId is { } account)
            tagMatch.Add("Lines.AccountId", new BsonBinaryData(account, GuidRepresentation.Standard));

        BsonDocument[] pipeline =
        [
            new("$match", match),
            new("$unwind", "$Lines"),
            new("$unwind", "$Lines.Dimensions"),
            new("$match", tagMatch),
            new("$group", new BsonDocument
            {
                { "_id", new BsonDocument { { "account", "$Lines.AccountId" }, { "dim", "$Lines.Dimensions.Value" } } },
                { "balance", new BsonDocument("$sum", SignedAmount()) },
            }),
        ];

        IAsyncCursor<BsonDocument> cursor =
            await _entries.AggregateAsync<BsonDocument>(pipeline, cancellationToken: cancellationToken);
        List<BsonDocument> results = await cursor.ToListAsync(cancellationToken);

        List<SubledgerBalance> balances = new(results.Count);
        foreach (BsonDocument doc in results)
        {
            BsonDocument id = doc["_id"].AsBsonDocument;
            Guid groupAccount = id["account"].AsBsonBinaryData.ToGuid(GuidRepresentation.Standard);
            Guid dimValue = id["dim"].AsBsonBinaryData.ToGuid(GuidRepresentation.Standard);
            balances.Add(new SubledgerBalance(groupAccount, dimValue, doc["balance"].AsDecimal));
        }

        return balances;
    }

    /// <summary>The on-the-books predicate shared by every balance fold: this client, Active and Posted.</summary>
    private static BsonDocument OnBooks(Guid clientId) => new()
    {
        { "ClientId", new BsonBinaryData(clientId, GuidRepresentation.Standard) },
        { "Status", nameof(LifecycleStatus.Active) },
        { "Posting", nameof(PostingState.Posted) },
    };

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// Unwind the matched entries' lines and sum the debit-positive signed effect per account
    /// (Debit => +Amount, Credit => -Amount). The caller supplies the <c>$match</c> stage; everything
    /// downstream is identical for a full trial balance and a windowed activity fold. When
    /// <paramref name="session"/> is non-null the aggregation runs inside that transaction's snapshot.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, decimal>> FoldAsync(
        BsonDocument match, IClientSessionHandle? session, CancellationToken cancellationToken)
    {
        BsonDocument[] pipeline =
        [
            new("$match", match),
            new("$unwind", "$Lines"),
            new("$group", new BsonDocument
            {
                { "_id", "$Lines.AccountId" },
                { "balance", new BsonDocument("$sum", SignedAmount()) },
            }),
        ];

        IAsyncCursor<BsonDocument> cursor = session is null
            ? await _entries.AggregateAsync<BsonDocument>(pipeline, cancellationToken: cancellationToken)
            : await _entries.AggregateAsync<BsonDocument>(session, pipeline, cancellationToken: cancellationToken);
        List<BsonDocument> results = await cursor.ToListAsync(cancellationToken);

        Dictionary<Guid, decimal> balances = new(results.Count);
        foreach (BsonDocument doc in results)
        {
            Guid accountId = doc["_id"].AsBsonBinaryData.ToGuid(GuidRepresentation.Standard);
            balances[accountId] = doc["balance"].AsDecimal;
        }

        return balances;
    }

    /// <summary>
    /// The debit-positive signed amount of an unwound line, as an aggregation expression:
    /// Debit =&gt; +Amount, Credit =&gt; -Amount. Shared by every fold so the trial balance, the windowed
    /// activity, and the subledger all measure the same thing.
    /// </summary>
    private static BsonDocument SignedAmount() => new("$cond", new BsonArray
    {
        new BsonDocument("$eq", new BsonArray { "$Lines.Direction", nameof(Direction.Debit) }),
        "$Lines.Amount",
        new BsonDocument("$multiply", new BsonArray { "$Lines.Amount", -1 }),
    });
}
