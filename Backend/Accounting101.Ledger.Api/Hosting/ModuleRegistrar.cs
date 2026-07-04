using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Api.Platform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Hosting;

/// <summary>
/// On startup, upserts the control-DB registration for each installed module into the DEFAULT firm's
/// control DB. Idempotent. (Per-firm module registration is a Phase 3 provisioning concern; today every
/// deployment has a single default firm.) Runs after <see cref="Platform.DefaultFirmSeeder"/>.
/// </summary>
public sealed class ModuleRegistrar(
    IEnumerable<ModuleRegistration> modules, PlatformStore platform,
    IMongoClientFactory factory, IConfiguration configuration) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Guid firmId = TenancyDefaults.ResolveDefaultFirmId(configuration);
        FirmRegistration? firm = await platform.GetFirmAsync(firmId, cancellationToken);
        if (firm is null)
            return;

        IMongoClient client = await factory.GetAsync(firm.ClusterKey, cancellationToken);
        ControlStore control = new(client.GetDatabase(firm.ControlDatabase));
        await control.SeedModulesAsync(modules, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
