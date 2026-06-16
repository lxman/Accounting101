using System.Globalization;
using MongoDB.Driver;

namespace Accounting101.Ledger.Mongo;

/// <summary>
/// Stores period-close checkpoints and answers "what's the latest close?" — the freeze
/// pointer the ledger uses to reject changes to closed periods.
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
        CancellationToken cancellationToken = default)
    {
        CheckpointDocument doc = new()
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            AsOf = asOf.ToString(DateFormat, CultureInfo.InvariantCulture),
            Balances = balances.ToDictionary(kv => kv.Key.ToString("N"), kv => kv.Value),
            ClosedBy = closedBy,
            ClosedAt = closedAt.UtcDateTime,
        };

        return _checkpoints.InsertOneAsync(doc, cancellationToken: cancellationToken);
    }

    /// <summary>The latest close date for a client (the freeze pointer), or null if never closed.</summary>
    public async Task<DateOnly?> GetClosedThroughAsync(Guid clientId, CancellationToken cancellationToken = default)
    {
        CheckpointDocument? latest = await _checkpoints
            .Find(c => c.ClientId == clientId)
            .SortByDescending(c => c.AsOf)
            .FirstOrDefaultAsync(cancellationToken);

        return latest is null ? null : DateOnly.ParseExact(latest.AsOf, DateFormat, CultureInfo.InvariantCulture);
    }

    /// <summary>The latest checkpoint's balances — the opening balance for the next period.</summary>
    public async Task<IReadOnlyDictionary<Guid, decimal>> GetLatestBalancesAsync(Guid clientId, CancellationToken cancellationToken = default)
    {
        CheckpointDocument? latest = await _checkpoints
            .Find(c => c.ClientId == clientId)
            .SortByDescending(c => c.AsOf)
            .FirstOrDefaultAsync(cancellationToken);

        return latest is null
            ? new Dictionary<Guid, decimal>()
            : latest.Balances.ToDictionary(kv => Guid.ParseExact(kv.Key, "N"), kv => kv.Value);
    }
}
