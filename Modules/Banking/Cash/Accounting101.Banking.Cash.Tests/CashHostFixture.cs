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

namespace Accounting101.Banking.Cash.Tests;

/// <summary>
/// Boots the real composition-root host (engine + Receivables + Payables + Payroll + Cash — all four
/// modules) against a disposable EphemeralMongo, with each module's loopback ledger HttpClient
/// repointed at the in-memory test server (no real socket). This is the live five-module composition
/// proof: all modules install themselves into one host, each keying its own document store by its
/// module key.
/// <para>
/// The configured posting-account ids are exposed so a test can seed a matching chart. Payables and
/// Payroll config is also supplied so the coexistence test can enter a bill/run alongside Cash.
/// </para>
/// </summary>
public sealed class CashHostFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private static readonly IReadOnlyList<string> DefaultModules = ["receivables", "payables", "payroll", "cash"];

    private string _connectionString = "";

    public IMongoClient Mongo { get; private set; } = null!;
    public string ControlDatabase { get; } = "control_" + Guid.NewGuid().ToString("N");
    public string PlatformDatabase { get; } = "platform_" + Guid.NewGuid().ToString("N");

    // Cash posting account.
    public Guid CashAccountId { get; } = Guid.NewGuid();

    // Line accounts used in disbursement tests.
    public Guid InterestExpenseAccountId { get; } = Guid.NewGuid();
    public Guid LoanPayableAccountId { get; } = Guid.NewGuid();

    // Line account used in deposit tests.
    public Guid MembersCapitalAccountId { get; } = Guid.NewGuid();

    // Payroll posting accounts (for coexistence proof).
    public Guid SalariesExpenseAccountId { get; } = Guid.NewGuid();
    public Guid PayrollTaxExpenseAccountId { get; } = Guid.NewGuid();
    public Guid WithholdingsPayableAccountId { get; } = Guid.NewGuid();
    public Guid PayrollTaxesPayableAccountId { get; } = Guid.NewGuid();

    // Payables posting accounts (for coexistence proof).
    public Guid PayableAccountId { get; } = Guid.NewGuid();
    public Guid RentExpenseAccountId { get; } = Guid.NewGuid();

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

        // Cash config.
        builder.UseSetting("Cash:Accounts:Cash", CashAccountId.ToString());

        // Payroll config (for coexistence proof — Payroll shares CashAccountId with Cash module).
        builder.UseSetting("Payroll:Accounts:SalariesExpense", SalariesExpenseAccountId.ToString());
        builder.UseSetting("Payroll:Accounts:PayrollTaxExpense", PayrollTaxExpenseAccountId.ToString());
        builder.UseSetting("Payroll:Accounts:Cash", CashAccountId.ToString());
        builder.UseSetting("Payroll:Accounts:WithholdingsPayable", WithholdingsPayableAccountId.ToString());
        builder.UseSetting("Payroll:Accounts:PayrollTaxesPayable", PayrollTaxesPayableAccountId.ToString());

        // Payables config (for coexistence proof).
        builder.UseSetting("Payables:Accounts:Payable", PayableAccountId.ToString());
        builder.UseSetting("Payables:Accounts:Cash", CashAccountId.ToString());

        // Repoint each module's loopback named/typed ledger client at this in-memory test server (no real
        // socket). Each module uses an explicit named client to avoid the ILedgerClient short-name collision.
        builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient("CashLedgerClient", c => c.BaseAddress = new Uri("http://localhost"))
                    .ConfigurePrimaryHttpMessageHandler(() => Server.CreateHandler());
            services.AddHttpClient("PayrollLedgerClient", c => c.BaseAddress = new Uri("http://localhost"))
                    .ConfigurePrimaryHttpMessageHandler(() => Server.CreateHandler());
            services.AddHttpClient("PayablesLedgerClient", c => c.BaseAddress = new Uri("http://localhost"))
                    .ConfigurePrimaryHttpMessageHandler(() => Server.CreateHandler());
        });
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

    /// <summary>Like <see cref="ClientFor"/>, but the token also carries the deployment-admin claim (admin=true).</summary>
    public HttpClient AdminClientFor(Guid userId, string name)
    {
        HttpClient http = CreateClient();
        string token = DevToken.Encode(new DevTokenPayload(userId, name, [new DevClaim("admin", "true")]));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(DevTokenDefaults.Scheme, token);
        return http;
    }

    /// <summary>Register a client + a single member with the given role, returning an HttpClient authed as that member.</summary>
    public async Task<(Guid ClientId, HttpClient Http)> SeedClientAsync(LedgerRole role)
    {
        Guid clientId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();
        ControlStore control = Control();
        await control.RegisterClientAsync(new ClientRegistration
        {
            Id = clientId, Name = "Acme", DatabaseName = "client_" + clientId.ToString("N"),
            EnabledModules = DefaultModules,
        });
        await control.AddMembershipAsync(userId, clientId, role);
        return (clientId, ClientFor(userId, $"Acme {role}"));
    }

    /// <summary>
    /// Register a SoD-ON client with three members: a Controller (chart setup, module document voids),
    /// a Clerk (records docs), and an Approver (approves GL entries, including reversals). Returns the
    /// client id and authed HttpClients for all three roles.
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
