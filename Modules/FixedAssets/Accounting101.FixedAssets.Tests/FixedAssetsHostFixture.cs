using System.Net.Http.Headers;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Control;
using Accounting101.TestSupport;
using EphemeralMongo;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace Accounting101.FixedAssets.Tests;

/// <summary>Boots the real composition-root host (engine + all modules incl. fixed assets) against a
/// disposable EphemeralMongo. FA-2 posts, so the module's named loopback ledger client is repointed at
/// the in-memory test server (no real socket).</summary>
public sealed class FixedAssetsHostFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private string _connectionString = "";

    public IMongoClient Mongo { get; private set; } = null!;
    public string ControlDatabase { get; } = "control_" + Guid.NewGuid().ToString("N");
    public string PlatformDatabase { get; } = "platform_" + Guid.NewGuid().ToString("N");

    // The two depreciation posting accounts.
    public Guid DepreciationExpenseAccountId { get; } = Guid.NewGuid();
    public Guid AccumulatedDepreciationAccountId { get; } = Guid.NewGuid();

    // The four additional disposal posting accounts.
    public Guid AssetCostAccountId { get; } = Guid.NewGuid();
    public Guid DisposalProceedsAccountId { get; } = Guid.NewGuid();
    public Guid GainOnDisposalAccountId { get; } = Guid.NewGuid();
    public Guid LossOnDisposalAccountId { get; } = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        IMongoRunner runner = await SharedMongo.InstanceAsync();
        _connectionString = runner.ConnectionString;
        Mongo = new MongoClient(_connectionString);
    }

    async Task IAsyncLifetime.DisposeAsync() => await ((IAsyncDisposable)this).DisposeAsync();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Mongo:ConnectionString", _connectionString);
        builder.UseSetting("Mongo:ControlDatabase", ControlDatabase);
        builder.UseSetting("Mongo:PlatformDatabase", PlatformDatabase);

        builder.UseSetting("FixedAssets:Accounts:DepreciationExpense", DepreciationExpenseAccountId.ToString());
        builder.UseSetting("FixedAssets:Accounts:AccumulatedDepreciation", AccumulatedDepreciationAccountId.ToString());
        builder.UseSetting("FixedAssets:Accounts:AssetCost", AssetCostAccountId.ToString());
        builder.UseSetting("FixedAssets:Accounts:DisposalProceeds", DisposalProceedsAccountId.ToString());
        builder.UseSetting("FixedAssets:Accounts:GainOnDisposal", GainOnDisposalAccountId.ToString());
        builder.UseSetting("FixedAssets:Accounts:LossOnDisposal", LossOnDisposalAccountId.ToString());

        builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient("FixedAssetsLedgerClient", c => c.BaseAddress = new Uri("http://localhost"))
                    .ConfigurePrimaryHttpMessageHandler(() => Server.CreateHandler());
        });
    }

    public ControlStore Control() => new(Mongo.GetDatabase(ControlDatabase));

    public HttpClient ClientFor(Guid userId, string name, LedgerRole role)
    {
        HttpClient http = CreateClient();
        string token = DevToken.Encode(new DevTokenPayload(userId, name, [new DevClaim("role", role.ToString())]));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(DevTokenDefaults.Scheme, token);
        return http;
    }

    /// <summary>Register a client (entitled to fixedassets by default) + a member with the given role,
    /// returning the client id and an HttpClient authed as that member.</summary>
    public async Task<(Guid ClientId, HttpClient Http)> SeedClientAsync(
        LedgerRole role = LedgerRole.Controller, IReadOnlyList<string>? enabledModules = null)
    {
        Guid clientId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();
        ControlStore control = Control();
        await control.RegisterClientAsync(new ClientRegistration
        {
            Id = clientId, Name = "Acme", DatabaseName = "client_" + clientId.ToString("N"),
            EnabledModules = enabledModules ?? ["fixedassets"],
        });
        await control.AddMembershipAsync(userId, clientId, role);
        return (clientId, ClientFor(userId, $"Acme {role}", role));
    }
}
