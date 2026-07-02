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

namespace Accounting101.Payables.Tests;

/// <summary>
/// Boots the real composition-root host (engine + payables) against a disposable EphemeralMongo, with
/// the payables→ledger loopback HttpClient repointed at the in-memory test server (no real socket).
/// The configured posting-account ids are exposed so a test can seed a matching chart.
/// </summary>
public sealed class PayablesHostFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private string _connectionString = "";

    public IMongoClient Mongo { get; private set; } = null!;
    public string ControlDatabase { get; } = "control_" + Guid.NewGuid().ToString("N");
    public Guid PayableAccountId { get; } = Guid.NewGuid();
    public Guid CashAccountId { get; } = Guid.NewGuid();
    public Guid VendorCreditsAccountId { get; } = Guid.NewGuid();
    public Guid RentExpenseAccountId { get; } = Guid.NewGuid();
    public Guid UtilitiesExpenseAccountId { get; } = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        IMongoRunner runner = await SharedMongo.InstanceAsync();
        _connectionString = runner.ConnectionString;
        Mongo = new MongoClient(_connectionString);
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await ((IAsyncDisposable)this).DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Mongo:ConnectionString", _connectionString);
        builder.UseSetting("Mongo:ControlDatabase", ControlDatabase);
        builder.UseSetting("Payables:Accounts:Payable", PayableAccountId.ToString());
        builder.UseSetting("Payables:Accounts:Cash", CashAccountId.ToString());
        builder.UseSetting("Payables:Accounts:VendorCredits", VendorCreditsAccountId.ToString());

        // Repoint the payables→ledger loopback named client at this in-memory test server (no real socket).
        // The name must match the one used in PayablesServiceExtensions to avoid a short-name collision
        // with the invoicing module's ILedgerClient.
        builder.ConfigureTestServices(services =>
            services.AddHttpClient("PayablesLedgerClient", c => c.BaseAddress = new Uri("http://localhost"))
                    .ConfigurePrimaryHttpMessageHandler(() => Server.CreateHandler()));
    }

    public ControlStore Control() => new(Mongo.GetDatabase(ControlDatabase));

    /// <summary>Mint an authenticated HttpClient for the given user.</summary>
    public HttpClient ClientFor(Guid userId, string name)
    {
        HttpClient http = CreateClient();
        string token = DevToken.Encode(new DevTokenPayload(userId, name, []));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(DevTokenDefaults.Scheme, token);
        return http;
    }

    /// <summary>Register a client + a Controller member, returning an HttpClient authed as that member.</summary>
    public async Task<(Guid ClientId, HttpClient Http)> SeedClientAsync()
    {
        Guid clientId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();
        ControlStore control = Control();
        await control.RegisterClientAsync(new ClientRegistration
        {
            Id = clientId, Name = "Acme", DatabaseName = "client_" + clientId.ToString("N"),
        });
        await control.AddMembershipAsync(userId, clientId, LedgerRole.Controller);
        return (clientId, ClientFor(userId, "Acme Controller"));
    }

    /// <summary>
    /// Register a SoD-ON client with three members: a Controller (chart setup, module document voids),
    /// a Clerk (enters bills/payments), and an Approver (approves GL entries, including reversals).
    /// Returns the client id and authed HttpClients for all three roles.
    /// </summary>
    public async Task<(Guid ClientId, HttpClient ControllerHttp, HttpClient ClerkHttp, HttpClient ApproverHttp)>
        SeedSodClientAsync()
    {
        Guid clientId = Guid.NewGuid();
        Guid controllerUserId = Guid.NewGuid();
        Guid clerkUserId = Guid.NewGuid();
        Guid approverUserId = Guid.NewGuid();
        ControlStore control = Control();
        await control.RegisterClientAsync(new ClientRegistration
        {
            Id = clientId, Name = "Acme SoD", DatabaseName = "client_" + clientId.ToString("N"),
            RequireSegregationOfDuties = true,
        });
        await control.AddMembershipAsync(controllerUserId, clientId, LedgerRole.Controller);
        await control.AddMembershipAsync(clerkUserId, clientId, LedgerRole.Clerk);
        await control.AddMembershipAsync(approverUserId, clientId, LedgerRole.Approver);
        return (clientId,
            ClientFor(controllerUserId, "Acme Controller"),
            ClientFor(clerkUserId, "Acme Clerk"),
            ClientFor(approverUserId, "Acme Approver"));
    }
}
