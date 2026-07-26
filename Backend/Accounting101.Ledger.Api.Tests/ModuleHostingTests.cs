using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Api.Hosting;
using Accounting101.Ledger.Api.Platform;
using Microsoft.Extensions.Configuration;
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
        // Seed the default firm pointing at the fixture's control DB, then run the registrar against it.
        PlatformStore platform = new(fixture.Mongo.GetDatabase(fixture.PlatformDatabase));
        await platform.RegisterFirmAsync(new FirmRegistration
        {
            Id = TenancyDefaults.DefaultFirmId, Name = "Default Firm",
            ControlDatabase = fixture.ControlDatabase, ClusterKey = "default",
        });
        // A factory whose home key "default" is pre-seeded to the fixture's client, so GetAsync("default")
        // needs no cluster-registry row.
        MongoClientFactory factory = new(fixture.Mongo, "default", platform);
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>()).Build();

        ModuleRegistrar registrar = new(
            [new ModuleRegistration { Key = "invoicing", Name = "Invoicing", Enabled = true }],
            platform, factory, config);
        await registrar.StartAsync(CancellationToken.None);

        ControlStore control = fixture.Control();
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

    /// <summary>
    /// The registration inside AddModule is a TryAdd, so only the FIRST AddModule call registers the
    /// factory — but the factory does not RUN until first resolution, long after every AddModule has
    /// completed. The single instance must therefore see every module, not just the first. This test is
    /// the guard against "fixing" it by computing the key set eagerly at registration time.
    /// </summary>
    [Fact]
    public void Installed_modules_sees_every_module_not_just_the_first_registered()
    {
        ServiceCollection services = new();
        services.AddModule(new ModuleIdentity("payables"), "Payables");
        services.AddModule(new ModuleIdentity("receivables"), "Receivables");

        using ServiceProvider provider = services.BuildServiceProvider();
        InstalledModules installed = provider.GetRequiredService<InstalledModules>();

        Assert.Equal(
            ["payables", "receivables"],
            installed.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.True(installed.Has("payables"));
        Assert.True(installed.Has("receivables"));
        Assert.False(installed.Has("einvoicing"));
    }

    /// <summary>
    /// A host that composes no module at all must still resolve an (empty) InstalledModules rather than
    /// throw — the engine registers the same late-bound factory, so first-wins TryAdd ordering between
    /// the engine and AddModule is irrelevant.
    /// </summary>
    [Fact]
    public void An_engine_with_no_modules_resolves_an_empty_installed_set()
    {
        ServiceCollection services = new();
        services.AddLedgerEngine(new ConfigurationBuilder().Build());

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Empty(provider.GetRequiredService<InstalledModules>().Keys);
    }
}
