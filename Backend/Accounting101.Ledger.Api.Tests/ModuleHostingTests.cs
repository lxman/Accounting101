using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Api.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>
/// A module installs itself by calling AddModule at host wiring time: that stamps its identity into
/// an in-process authenticator and contributes a registration the startup registrar upserts into the
/// control DB — so "installed" (a wired Add line) becomes a registered, enabled module row.
/// </summary>
public sealed class ModuleHostingTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public void AddModule_stamps_an_in_process_authenticator_for_the_identity()
    {
        ServiceCollection services = new();
        services.AddModule(new ModuleIdentity("invoicing"), "Invoicing");

        using ServiceProvider provider = services.BuildServiceProvider();
        ModuleIdentity? identity = provider.GetRequiredService<IModuleAuthenticator>().Authenticate();
        Assert.Equal(new ModuleIdentity("invoicing"), identity);
    }

    [Fact]
    public async Task The_registrar_upserts_contributed_registrations_on_startup()
    {
        ControlStore control = fixture.Control();
        ModuleRegistrar registrar = new(
            [new ModuleRegistration { Key = "invoicing", Name = "Invoicing", Enabled = true }],
            control);

        await registrar.StartAsync(CancellationToken.None);

        ModuleRegistration? registered = await control.GetModuleAsync("invoicing");
        Assert.NotNull(registered);
        Assert.True(registered!.Enabled);
    }
}
