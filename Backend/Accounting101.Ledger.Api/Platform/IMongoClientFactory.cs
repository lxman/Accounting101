using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Platform;

/// <summary>
/// Resolves a cluster key to the pooled <see cref="IMongoClient"/> for that cluster. This is the seam
/// that lets firms spread across multiple Atlas clusters later without the engine knowing clusters exist:
/// today every firm resolves to the home cluster; adding cluster #2 is a registry row, not a code change.
/// </summary>
public interface IMongoClientFactory
{
    Task<IMongoClient> GetAsync(string clusterKey, CancellationToken cancellationToken = default);
}
