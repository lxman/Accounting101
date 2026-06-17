using System.Net.Http.Headers;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Control;
using EphemeralMongo;
using Microsoft.AspNetCore.Hosting;
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
    private IMongoRunner _runner = null!;
    private WebApplicationFactory<Program> _factory = null!;

    public IMongoClient Mongo { get; private set; } = null!;
    public string ControlDatabase { get; } = "control_" + Guid.NewGuid().ToString("N");

    public async Task InitializeAsync()
    {
        MongoRunnerOptions options = new()
        {
            Version = MongoVersion.V8,
            UseSingleNodeReplicaSet = true,
            AdditionalArguments = ["--quiet"],
            StandardErrorLogger = Console.Error.WriteLine,
        };
        _runner = await MongoRunner.RunAsync(options);
        Mongo = new MongoClient(_runner.ConnectionString);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseSetting("Mongo:ConnectionString", _runner.ConnectionString)
             .UseSetting("Mongo:ControlDatabase", ControlDatabase));
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        _runner?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>A control-store bound to the same control DB the app reads — for seeding clients/memberships.</summary>
    public ControlStore Control() => new(Mongo.GetDatabase(ControlDatabase));

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
}
