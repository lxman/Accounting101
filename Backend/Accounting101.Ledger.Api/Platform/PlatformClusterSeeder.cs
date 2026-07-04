using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Accounting101.Ledger.Api.Platform;

/// <summary>
/// Records the home cluster (the process connection string, under the home cluster key) in the platform
/// registry on startup, idempotently. This makes the home cluster discoverable through the same registry
/// as any future cluster, so tooling and the resolver treat "default" like every other key.
/// </summary>
public sealed class PlatformClusterSeeder(PlatformStore platform, IConfiguration configuration) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        string key = configuration["Mongo:ClusterKey"] ?? "default";
        string connectionString = configuration["Mongo:ConnectionString"] ?? "mongodb://localhost:27017";
        return platform.RegisterClusterAsync(
            new ClusterRegistration { Key = key, ConnectionString = connectionString }, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
