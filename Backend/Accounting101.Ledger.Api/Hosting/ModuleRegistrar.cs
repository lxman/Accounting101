using Accounting101.Ledger.Api.Control;
using Microsoft.Extensions.Hosting;

namespace Accounting101.Ledger.Api.Hosting;

/// <summary>
/// On startup, upserts the control-DB registration for each installed module (every module that
/// called <see cref="ModuleHostingExtensions.AddModule"/>). Idempotent — re-running on each boot
/// reconciles the registry to what the host actually has wired.
/// </summary>
public sealed class ModuleRegistrar(IEnumerable<ModuleRegistration> modules, ControlStore control) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (ModuleRegistration module in modules)
            await control.RegisterModuleAsync(module, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
