using System.Globalization;
using Accounting101.Ledger.Mongo.Documents;
using Accounting101.Ledger.Mongo.Serialization;
using MongoDB.Driver;

namespace Accounting101.Ledger.Mongo;

/// <summary>
/// Stores the single period-close checkpoint per client — the freeze pointer ("nothing on or
/// before this date may change") and the opening balances for the open period. At most one per
/// client; each close replaces it. The history of who closed when lives in the audit log, and
/// any past period-end balance is recomputable from the journal via an as-of aggregation.
/// </summary>
public sealed class MongoCheckpointStore
{
    private const string DateFormat = "yyyy-MM-dd";

    private readonly IMongoCollection<CheckpointDocument> _checkpoints;

    static MongoCheckpointStore() => LedgerMongoBootstrap.RegisterOnce();

    public MongoCheckpointStore(IMongoDatabase database, string collectionName = "checkpoints")
    {
        ArgumentNullException.ThrowIfNull(database);
        _checkpoints = database.GetCollection<CheckpointDocument>(collectionName);
    }

    public Task SaveAsync(
        Guid clientId,
        DateOnly asOf,
        IReadOnlyDictionary<Guid, decimal> balances,
        Guid closedBy,
        DateTimeOffset closedAt,
        IClientSessionHandle? session = null,
        CancellationToken cancellationToken = default)
    {
        CheckpointDocument doc = new()
        {
            ClientId = clientId,
            AsOf = asOf.ToString(DateFormat, CultureInfo.InvariantCulture),
            Balances = balances.ToDictionary(kv => kv.Key.ToString("N"), kv => kv.Value),
            ClosedBy = closedBy,
            ClosedAt = closedAt.UtcDateTime,
        };

        // One checkpoint per client — each close replaces the previous.
        FilterDefinition<CheckpointDocument> filter = Builders<CheckpointDocument>.Filter.Where(c => c.ClientId == clientId);
        ReplaceOptions options = new() { IsUpsert = true };
        return session is null
            ? _checkpoints.ReplaceOneAsync(filter, doc, options, cancellationToken)
            : _checkpoints.ReplaceOneAsync(session, filter, doc, options, cancellationToken);
    }

    /// <summary>Remove the client's checkpoint entirely — a full reopen (no period frozen).</summary>
    public Task DeleteAsync(Guid clientId, IClientSessionHandle? session = null, CancellationToken cancellationToken = default)
    {
        FilterDefinition<CheckpointDocument> filter = Builders<CheckpointDocument>.Filter.Where(c => c.ClientId == clientId);
        return session is null
            ? _checkpoints.DeleteOneAsync(filter, cancellationToken)
            : _checkpoints.DeleteOneAsync(session, filter, cancellationToken: cancellationToken);
    }

    /// <summary>The client's close date (the freeze pointer), or null if never closed.</summary>
    public async Task<DateOnly?> GetClosedThroughAsync(Guid clientId, CancellationToken cancellationToken = default)
    {
        CheckpointDocument? checkpoint = await _checkpoints
            .Find(c => c.ClientId == clientId)
            .FirstOrDefaultAsync(cancellationToken);

        return checkpoint is null ? null : DateOnly.ParseExact(checkpoint.AsOf, DateFormat, CultureInfo.InvariantCulture);
    }

    /// <summary>The client's close date read through <paramref name="session"/> (the transactional snapshot).</summary>
    public async Task<DateOnly?> GetClosedThroughAsync(Guid clientId, IClientSessionHandle session, CancellationToken cancellationToken = default)
    {
        CheckpointDocument? checkpoint = await _checkpoints
            .Find(session, c => c.ClientId == clientId)
            .FirstOrDefaultAsync(cancellationToken);

        return checkpoint is null ? null : DateOnly.ParseExact(checkpoint.AsOf, DateFormat, CultureInfo.InvariantCulture);
    }

    /// <summary>The checkpoint's balances — the opening balance for the open period.</summary>
    public async Task<IReadOnlyDictionary<Guid, decimal>> GetOpeningBalancesAsync(Guid clientId, CancellationToken cancellationToken = default)
    {
        CheckpointDocument? checkpoint = await _checkpoints
            .Find(c => c.ClientId == clientId)
            .FirstOrDefaultAsync(cancellationToken);

        return checkpoint is null
            ? new Dictionary<Guid, decimal>()
            : checkpoint.Balances.ToDictionary(kv => Guid.ParseExact(kv.Key, "N"), kv => kv.Value);
    }
}
