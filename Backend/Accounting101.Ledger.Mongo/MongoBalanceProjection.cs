using Accounting101.Ledger.Core.Journal;
using MongoDB.Driver;

namespace Accounting101.Ledger.Mongo;

/// <summary>
/// Maintained trial-balance read model: one document per client, updated
/// incrementally as on-the-books entries are posted, so balance reads are O(1)
/// point lookups instead of an O(N) journal scan. It is a <em>rebuildable cache</em>
/// — <see cref="RebuildAsync"/> recomputes it from the journal (the source of truth),
/// which is also the integrity cross-check.
/// </summary>
public sealed class MongoBalanceProjection
{
    private readonly IMongoCollection<ClientBalancesDocument> _balances;
    private readonly MongoJournalStore _journal;

    static MongoBalanceProjection() => LedgerMongoBootstrap.RegisterOnce();

    public MongoBalanceProjection(IMongoDatabase database, MongoJournalStore journal, string collectionName = "balances")
    {
        ArgumentNullException.ThrowIfNull(database);
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _balances = database.GetCollection<ClientBalancesDocument>(collectionName);
    }

    /// <summary>
    /// Applies an on-the-books entry's per-account deltas to the client's balance
    /// document (upsert). No-op for entries that are not <see cref="LedgerReplay.IsOnBooks"/>
    /// (e.g. pending approval). Lines to the same account are netted into one increment.
    /// </summary>
    public Task ApplyAsync(JournalEntry entry, CancellationToken cancellationToken = default) =>
        UpdateAsync(entry, +1, cancellationToken);

    /// <summary>
    /// Reverse an entry's deltas — used when an on-the-books entry is voided or superseded.
    /// No-op if the entry was not on the books.
    /// </summary>
    public Task ReverseAsync(JournalEntry entry, CancellationToken cancellationToken = default) =>
        UpdateAsync(entry, -1, cancellationToken);

    private Task UpdateAsync(JournalEntry entry, int sign, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!LedgerReplay.IsOnBooks(entry))
            return Task.CompletedTask;

        UpdateDefinitionBuilder<ClientBalancesDocument> update = Builders<ClientBalancesDocument>.Update;
        List<UpdateDefinition<ClientBalancesDocument>> increments = entry.Lines
            .GroupBy(line => line.AccountId)
            .Select(group => update.Inc($"Balances.{group.Key:N}", sign * group.Sum(line => line.SignedEffect)))
            .ToList();

        return _balances.UpdateOneAsync(
            balances => balances.ClientId == entry.ClientId,
            update.Combine(increments),
            new UpdateOptions { IsUpsert = true },
            cancellationToken);
    }

    /// <summary>O(1): the whole trial balance for a client (empty if none recorded yet).</summary>
    public async Task<IReadOnlyDictionary<Guid, decimal>> GetTrialBalanceAsync(
        Guid clientId, CancellationToken cancellationToken = default)
    {
        ClientBalancesDocument? doc = await _balances
            .Find(balances => balances.ClientId == clientId)
            .FirstOrDefaultAsync(cancellationToken);

        return doc is null
            ? new Dictionary<Guid, decimal>()
            : doc.Balances.ToDictionary(kv => Guid.ParseExact(kv.Key, "N"), kv => kv.Value);
    }

    /// <summary>O(1): one account's balance for a client (zero if untouched).</summary>
    public async Task<decimal> GetBalanceAsync(Guid clientId, Guid accountId, CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<Guid, decimal> balances = await GetTrialBalanceAsync(clientId, cancellationToken);
        return balances.GetValueOrDefault(accountId);
    }

    /// <summary>
    /// Authoritative repair/initialize: recompute the client's trial balance from the
    /// journal (server-side aggregation) and overwrite the projection document.
    /// </summary>
    public async Task RebuildAsync(Guid clientId, CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<Guid, decimal> balances = await _journal.AggregateBalancesAsync(clientId, cancellationToken: cancellationToken);

        ClientBalancesDocument doc = new()
        {
            ClientId = clientId,
            Balances = balances.ToDictionary(kv => kv.Key.ToString("N"), kv => kv.Value),
        };

        await _balances.ReplaceOneAsync(
            balances => balances.ClientId == clientId,
            doc,
            new ReplaceOptions { IsUpsert = true },
            cancellationToken);
    }
}
