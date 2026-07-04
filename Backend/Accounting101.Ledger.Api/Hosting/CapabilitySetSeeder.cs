using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Api.Platform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Hosting;

/// <summary>Seeds the built-in capability sets and backfills legacy role grants into the DEFAULT firm's
/// control DB on startup (idempotent). Runs after <see cref="Platform.DefaultFirmSeeder"/>, which
/// guarantees the default firm exists.</summary>
public sealed class CapabilitySetSeeder(
    PlatformStore platform, IMongoClientFactory factory, IConfiguration configuration) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Guid firmId = TenancyDefaults.ResolveDefaultFirmId(configuration);
        FirmRegistration? firm = await platform.GetFirmAsync(firmId, cancellationToken);
        if (firm is null)
            return; // DefaultFirmSeeder runs first; nothing to seed if the default firm is absent.

        IMongoClient client = await factory.GetAsync(firm.ClusterKey, cancellationToken);
        ControlStore control = new(client.GetDatabase(firm.ControlDatabase));
        await control.SeedBuiltinCapabilitySetsAsync(cancellationToken);
        await control.BackfillGrantedSetIdsAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
