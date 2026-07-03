using Accounting101.Ledger.Api.Control;
using Microsoft.Extensions.Hosting;

namespace Accounting101.Ledger.Api.Hosting;

/// <summary>Seeds the built-in capability sets into the control DB on startup (idempotent,
/// persist-in-place). Runs once per host start.</summary>
public sealed class CapabilitySetSeeder(ControlStore control) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        control.SeedBuiltinCapabilitySetsAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
