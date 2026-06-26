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
    public async Task AddModule_stamps_an_in_process_authenticator_for_the_identity()
    {
        ServiceCollection services = new();
        services.AddModule(new ModuleIdentity("invoicing"), "Invoicing");

        using ServiceProvider provider = services.BuildServiceProvider();
        // The host-stamped authenticator is registered under the "host-stamped" key; the default
        // IModuleAuthenticator (credential-verifying, for the HTTP pipeline) is scoped separately.
        IModuleAuthenticator stamped = provider.GetRequiredKeyedService<IModuleAuthenticator>("host-stamped");
        ModuleIdentity? identity = await stamped.AuthenticateAsync();
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

    /// <summary>
    /// Proves that keyed credential registration is order-independent: even when a second module
    /// (e.g. "receivables") is registered AFTER "payables", resolving the "payables" key always
    /// returns the payables credential — never the receivables one. This test would fail if the
    /// credential were registered unkeyed (last-wins collision).
    /// </summary>
    [Fact]
    public void Keyed_credential_resolves_correct_module_regardless_of_registration_order()
    {
        ServiceCollection services = new();

        // Register payables FIRST, then receivables — mirrors the real host order.
        services.AddModule(new ModuleIdentity("payables"), "Payables");
        services.AddModule(new ModuleIdentity("receivables"), "Receivables");

        using ServiceProvider provider = services.BuildServiceProvider();

        ModuleCredential payablesCred = provider.GetRequiredKeyedService<ModuleCredential>("payables");
        ModuleCredential receivablesCred = provider.GetRequiredKeyedService<ModuleCredential>("receivables");

        // Each module must get its own credential — keys must differ.
        Assert.Equal("payables", payablesCred.Key);
        Assert.Equal("receivables", receivablesCred.Key);

        // The two instances must be distinct objects (not the same shared singleton).
        Assert.NotSame(payablesCred, receivablesCred);
    }
}
