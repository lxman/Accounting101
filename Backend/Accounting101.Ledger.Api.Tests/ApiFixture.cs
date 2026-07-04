using System.Net.Http.Headers;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Api.Platform;
using Accounting101.TestSupport;
using EphemeralMongo;
using Microsoft.AspNetCore.Mvc.Testing;
using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>
/// Boots the API in-memory (<see cref="WebApplicationFactory{T}"/>) against a disposable
/// EphemeralMongo single-node replica set. The control database and every client database live in
/// that one Mongo instance under distinct names, so tests exercise tenant resolution and isolation
/// for real — the same code path production uses, only the Mongo location differs.
/// </summary>
public sealed class ApiFixture : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;

    public IMongoClient Mongo { get; private set; } = null!;
    public string ControlDatabase { get; } = "control_" + Guid.NewGuid().ToString("N");
    public string PlatformDatabase { get; } = "platform_" + Guid.NewGuid().ToString("N");

    public async Task InitializeAsync()
    {
        IMongoRunner runner = await SharedMongo.InstanceAsync();
        string connectionString = runner.ConnectionString;
        Mongo = new MongoClient(connectionString);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseSetting("Mongo:ConnectionString", connectionString)
             .UseSetting("Mongo:ControlDatabase", ControlDatabase)
             .UseSetting("Mongo:PlatformDatabase", PlatformDatabase)
             .UseSetting("Tenancy:Platform:Enabled", "true"));
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>A control-store bound to the same control DB the app reads — for seeding clients/memberships.</summary>
    public ControlStore Control() => new(Mongo.GetDatabase(ControlDatabase));

    /// <summary>An audit store bound to the same control DB the app writes — for asserting audit entries.</summary>
    public AdminAuditStore Audit() => new(Mongo.GetDatabase(ControlDatabase));

    public IMongoDatabase ClientDatabase(string name) => Mongo.GetDatabase(name);

    /// <summary>An HttpClient whose Authorization header carries a dev token for the given principal.</summary>
    public HttpClient ClientFor(Guid userId, string? name = null, params (string Type, string Value)[] claims)
    {
        HttpClient client = _factory.CreateClient();
        string token = DevToken.Encode(new DevTokenPayload(
            userId, name, claims.Select(c => new DevClaim(c.Type, c.Value)).ToList()));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(DevTokenDefaults.Scheme, token);
        return client;
    }

    public HttpClient AnonymousClient() => _factory.CreateClient();

    /// <summary>An HttpClient authenticated as a deployment admin (carries the provisioning claim).</summary>
    public HttpClient AdminClient() => ClientFor(Guid.NewGuid(), "Deployment Admin", ("admin", "true"));

    /// <summary>Register a fresh client plus a member user, returning an HttpClient authed as that user.</summary>
    public async Task<SeededClient> SeedClientAsync(
        string name = "Acme", bool requireSod = false, LedgerRole role = LedgerRole.Controller,
        IReadOnlyList<string>? enabledModules = null)
    {
        Guid clientId = Guid.NewGuid();
        string database = "client_" + clientId.ToString("N");
        Guid userId = Guid.NewGuid();

        // Mint the HttpClient FIRST: WebApplicationFactory boots the host lazily, on first client
        // creation, and host boot is what runs ModuleRegistrar (an IHostedService) to auto-register
        // every installed module. Querying ListModulesAsync for the default entitlement snapshot
        // before that point would see none of them — the client would be seeded entitled to nothing.
        HttpClient http = ClientFor(userId, $"{name} {role}", ("role", role.ToString()));

        ControlStore control = Control();
        IReadOnlyList<string> modules = enabledModules ?? (await control.ListModulesAsync()).Select(m => m.Key).ToList();
        await control.RegisterClientAsync(new ClientRegistration
        {
            Id = clientId,
            Name = name,
            DatabaseName = database,
            RequireSegregationOfDuties = requireSod,
            EnabledModules = modules,
        });
        await control.AddMembershipAsync(userId, clientId, role);

        return new SeededClient(clientId, database, userId, http);
    }

    /// <summary>Add another member user (with a role) to a client and return an HttpClient authed as them.</summary>
    public async Task<HttpClient> AddMemberAsync(Guid clientId, LedgerRole role = LedgerRole.Controller, string name = "Member")
    {
        Guid userId = Guid.NewGuid();
        await Control().AddMembershipAsync(userId, clientId, role);
        return ClientFor(userId, name, ("role", role.ToString()));
    }

    /// <summary>Register a second firm (its own control DB) in this fixture's platform registry, so a test
    /// can prove cross-firm isolation. Returns the firm id and its control database name.</summary>
    public async Task<(Guid FirmId, string ControlDatabase)> SeedFirmAsync(string name)
    {
        Guid firmId = Guid.NewGuid();
        string controlDatabase = "firm_" + firmId.ToString("N") + "_control";
        PlatformStore platform = new(Mongo.GetDatabase(PlatformDatabase));
        await platform.RegisterFirmAsync(new FirmRegistration
        {
            Id = firmId, Name = name, ControlDatabase = controlDatabase, ClusterKey = "default",
        });
        return (firmId, controlDatabase);
    }
}

/// <summary>A registered client plus a member user and an HttpClient authenticated as that user.</summary>
public sealed record SeededClient(Guid ClientId, string Database, Guid UserId, HttpClient Http);
