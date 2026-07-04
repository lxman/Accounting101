using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Platform;

/// <summary>
/// Records the home cluster (the process connection string, under the home cluster key) in the platform
/// registry on startup, idempotently. This makes the home cluster discoverable through the same registry
/// as any future cluster, so tooling and the resolver treat "default" like every other key.
/// </summary>
public sealed class PlatformClusterSeeder(PlatformStore platform, IConfiguration configuration) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        string key = configuration["Mongo:ClusterKey"] ?? "default";
        string connectionString = configuration["Mongo:ConnectionString"] ?? "mongodb://localhost:27017";
        try
        {
            await platform.RegisterClusterAsync(
                new ClusterRegistration { Key = key, ConnectionString = connectionString }, cancellationToken);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // Another instance seeded the home cluster concurrently on a fresh platform_control; the row now
            // exists with the same value, so the duplicate-key race is a successful outcome.
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
