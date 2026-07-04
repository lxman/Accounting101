using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Platform;

/// <summary>
/// Registers the deployment's single (default) firm on startup, pointing at the configured control DB and
/// home cluster, idempotently. This is what makes today's one-control-DB deployment "the default firm's
/// control DB", so requests with no firm claim resolve to it. Leaves an existing default firm untouched
/// (an operator may have re-pointed it) and tolerates the concurrent-cold-start duplicate-key race.
/// </summary>
public sealed class DefaultFirmSeeder(PlatformStore platform, IConfiguration configuration) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Guid firmId = TenancyDefaults.ResolveDefaultFirmId(configuration);
        if (await platform.GetFirmAsync(firmId, cancellationToken) is not null)
            return;

        string controlDatabase = configuration["Mongo:ControlDatabase"] ?? "ledger_control";
        string clusterKey = configuration["Mongo:ClusterKey"] ?? "default";
        try
        {
            await platform.RegisterFirmAsync(new FirmRegistration
            {
                Id = firmId,
                Name = "Default Firm",
                ControlDatabase = controlDatabase,
                ClusterKey = clusterKey,
                CreatedUtc = DateTime.UtcNow,
            }, cancellationToken);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // Another instance seeded the default firm concurrently on a fresh platform_control — success.
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
