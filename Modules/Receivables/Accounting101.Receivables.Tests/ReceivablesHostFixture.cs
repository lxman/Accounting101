using System.Net.Http.Headers;
using Accounting101.Receivables.Api;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Control;
using Accounting101.TestSupport;
using EphemeralMongo;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace Accounting101.Receivables.Tests;

/// <summary>
/// Boots the real composition-root host (engine + receivables) against a disposable EphemeralMongo, with
/// the receivables→ledger loopback HttpClient repointed at the in-memory test server (no real socket).
/// The configured posting-account ids are exposed so a test can seed a matching chart.
/// </summary>
public sealed class ReceivablesHostFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private static readonly IReadOnlyList<string> DefaultModules = ["receivables"];

    private string _connectionString = "";

    public IMongoClient Mongo { get; private set; } = null!;
    public string ControlDatabase { get; } = "control_" + Guid.NewGuid().ToString("N");
    public string PlatformDatabase { get; } = "platform_" + Guid.NewGuid().ToString("N");
    public Guid ReceivableAccountId { get; } = Guid.NewGuid();
    public Guid RevenueAccountId { get; } = Guid.NewGuid();
    public Guid LicenseRevenueAccountId { get; } = Guid.NewGuid();
    public Guid SalesTaxPayableAccountId { get; } = Guid.NewGuid();
    public Guid CashAccountId { get; } = Guid.NewGuid();
    public Guid CustomerCreditsAccountId { get; } = Guid.NewGuid();
    public Guid BadDebtExpenseAccountId { get; } = Guid.NewGuid();
    public Guid SalesReturnsAccountId { get; } = Guid.NewGuid();

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
        builder.UseSetting("Mongo:PlatformDatabase", PlatformDatabase);
        builder.UseSetting("Receivables:Accounts:Receivable", ReceivableAccountId.ToString());
        builder.UseSetting("Receivables:Accounts:Revenue", RevenueAccountId.ToString());
        builder.UseSetting("Receivables:Accounts:RevenueByCategory:License", LicenseRevenueAccountId.ToString());
        builder.UseSetting("Receivables:Accounts:SalesTaxPayable", SalesTaxPayableAccountId.ToString());
        builder.UseSetting("Receivables:Accounts:Cash", CashAccountId.ToString());
        builder.UseSetting("Receivables:Accounts:CustomerCredits", CustomerCreditsAccountId.ToString());
        builder.UseSetting("Receivables:Accounts:BadDebtExpense", BadDebtExpenseAccountId.ToString());
        builder.UseSetting("Receivables:Accounts:SalesReturns", SalesReturnsAccountId.ToString());

        // Repoint the receivables→ledger loopback client at this in-memory test server (no real socket).
        builder.ConfigureTestServices(services =>
            services.AddHttpClient<ILedgerClient, HttpLedgerClient>(c => c.BaseAddress = new Uri("http://localhost"))
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

    /// <summary>An HttpClient authenticated as a deployment admin (carries the "admin" claim).</summary>
    public HttpClient AdminClient()
    {
        HttpClient http = CreateClient();
        string token = DevToken.Encode(new DevTokenPayload(
            Guid.NewGuid(), "Deployment Admin", [new DevClaim("admin", "true")]));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(DevTokenDefaults.Scheme, token);
        return http;
    }

    /// <summary>Like <see cref="ClientFor"/>, but the token also carries the deployment-admin claim (admin=true).</summary>
    public HttpClient AdminClientFor(Guid userId, string name)
    {
        HttpClient http = CreateClient();
        string token = DevToken.Encode(new DevTokenPayload(userId, name, [new DevClaim("admin", "true")]));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(DevTokenDefaults.Scheme, token);
        return http;
    }

    /// <summary>Register a client + a Controller member, returning an HttpClient authed as that member.</summary>
    public async Task<(Guid ClientId, HttpClient Http)> SeedClientAsync(IReadOnlyList<string>? enabledModules = null)
    {
        Guid clientId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();
        ControlStore control = Control();
        await control.RegisterClientAsync(new ClientRegistration
        {
            Id = clientId, Name = "Acme", DatabaseName = "client_" + clientId.ToString("N"),
            EnabledModules = enabledModules ?? DefaultModules,
        });
        await control.AddMembershipAsync(userId, clientId, LedgerRole.Controller);
        return (clientId, ClientFor(userId, "Acme Controller"));
    }

    /// <summary>
    /// Register a SoD-ON client with three members: a Controller (chart setup, module document voids),
    /// a Clerk (issues), and an Approver (approves GL entries, including reversals). Returns the client
    /// id and authed HttpClients for the Clerk and Approver; use a separate Controller client for chart
    /// setup via <see cref="SeedClientAsync"/> or drive it through the returned controller client directly.
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
            EnabledModules = DefaultModules,
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
