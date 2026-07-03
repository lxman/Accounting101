using Accounting101.Ledger.Api.Control;
using Microsoft.Extensions.Hosting;

namespace Accounting101.Ledger.Api.Hosting;

/// <summary>Seeds the built-in capability sets and backfills legacy role grants to set references on
/// startup (idempotent).</summary>
public sealed class CapabilitySetSeeder(ControlStore control) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await control.SeedBuiltinCapabilitySetsAsync(cancellationToken);
        await control.BackfillGrantedSetIdsAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
