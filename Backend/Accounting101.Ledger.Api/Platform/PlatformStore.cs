using Accounting101.Ledger.Mongo.Serialization;
using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Platform;

/// <summary>
/// Persistence for the platform control database (one per SaaS install): the firm registry
/// (firm id → control DB + cluster) and the cluster registry (cluster key → connection string). This is
/// the tier above every firm's control DB. On an on-site deployment it holds exactly one firm.
/// </summary>
public sealed class PlatformStore
{
    private readonly IMongoCollection<FirmRegistration> _firms;
    private readonly IMongoCollection<ClusterRegistration> _clusters;

    static PlatformStore() => LedgerMongoBootstrap.RegisterOnce();

    public PlatformStore(IMongoDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _firms = database.GetCollection<FirmRegistration>("firms");
        _clusters = database.GetCollection<ClusterRegistration>("clusters");
    }

    /// <summary>The firm's registration, or null if no such firm exists.</summary>
    public async Task<FirmRegistration?> GetFirmAsync(Guid firmId, CancellationToken cancellationToken = default) =>
        await _firms.Find(f => f.Id == firmId).FirstOrDefaultAsync(cancellationToken);

    /// <summary>Register (or update) a firm — idempotent upsert keyed by firm id.</summary>
    public Task RegisterFirmAsync(FirmRegistration firm, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(firm);
        return _firms.ReplaceOneAsync(
            f => f.Id == firm.Id, firm, new ReplaceOptions { IsUpsert = true }, cancellationToken);
    }

    /// <summary>All firms registered on the platform.</summary>
    public async Task<IReadOnlyList<FirmRegistration>> ListFirmsAsync(CancellationToken cancellationToken = default) =>
        await _firms.Find(FilterDefinition<FirmRegistration>.Empty).ToListAsync(cancellationToken);

    /// <summary>Set a firm's lifecycle status (e.g. suspend on non-payment).</summary>
    public Task SetFirmStatusAsync(Guid firmId, FirmStatus status, CancellationToken cancellationToken = default) =>
        _firms.UpdateOneAsync(
            f => f.Id == firmId,
            Builders<FirmRegistration>.Update.Set(f => f.Status, status),
            cancellationToken: cancellationToken);

    /// <summary>The cluster's registration, or null if no such cluster key is registered.</summary>
    public async Task<ClusterRegistration?> GetClusterAsync(string key, CancellationToken cancellationToken = default) =>
        await _clusters.Find(c => c.Key == key).FirstOrDefaultAsync(cancellationToken);

    /// <summary>Register (or update) a cluster — idempotent upsert keyed by cluster key.</summary>
    public Task RegisterClusterAsync(ClusterRegistration cluster, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        return _clusters.ReplaceOneAsync(
            c => c.Key == cluster.Key, cluster, new ReplaceOptions { IsUpsert = true }, cancellationToken);
    }

    /// <summary>All registered clusters.</summary>
    public async Task<IReadOnlyList<ClusterRegistration>> ListClustersAsync(CancellationToken cancellationToken = default) =>
        await _clusters.Find(FilterDefinition<ClusterRegistration>.Empty).ToListAsync(cancellationToken);
}
