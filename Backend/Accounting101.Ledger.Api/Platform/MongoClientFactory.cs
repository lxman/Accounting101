using System.Collections.Concurrent;
using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Platform;

/// <summary>
/// Pools one <see cref="IMongoClient"/> per cluster key. The home cluster reuses the process client
/// (pre-seeded in the constructor) so we never open a second pool to the same server; every other key is
/// resolved through <see cref="PlatformStore"/> and its client is built once and cached. An unregistered
/// key throws — the factory refuses to invent a connection.
/// </summary>
public sealed class MongoClientFactory : IMongoClientFactory
{
    private readonly PlatformStore _platform;
    private readonly ConcurrentDictionary<string, Lazy<IMongoClient>> _clients = new();

    public MongoClientFactory(IMongoClient homeClient, string homeClusterKey, PlatformStore platform)
    {
        ArgumentNullException.ThrowIfNull(homeClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(homeClusterKey);
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _clients[homeClusterKey] = new Lazy<IMongoClient>(() => homeClient);
    }

    public async Task<IMongoClient> GetAsync(string clusterKey, CancellationToken cancellationToken = default)
    {
        if (_clients.TryGetValue(clusterKey, out Lazy<IMongoClient>? cached))
            return cached.Value;

        ClusterRegistration? cluster = await _platform.GetClusterAsync(clusterKey, cancellationToken);
        if (cluster is null)
            throw new InvalidOperationException($"No cluster registered for key '{clusterKey}'.");

        // GetOrAdd collapses a concurrent double-build to a single cached client; any loser's Lazy is
        // discarded without ever building (a MongoClient starts server-monitoring at construction, so an
        // unbuilt Lazy leaks nothing).
        return _clients.GetOrAdd(clusterKey, new Lazy<IMongoClient>(() => new MongoClient(cluster.ConnectionString))).Value;
    }
}
