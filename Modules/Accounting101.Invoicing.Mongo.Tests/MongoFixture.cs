using Accounting101.Invoicing.Mongo;
using EphemeralMongo;
using MongoDB.Driver;

namespace Accounting101.Invoicing.Mongo.Tests;

/// <summary>
/// A disposable, self-contained MongoDB (via EphemeralMongo) shared across a test class — the same
/// approach the engine uses, so the module's persistence tests need no external Mongo.
/// </summary>
public sealed class MongoFixture : IAsyncLifetime
{
    private IMongoRunner _runner = null!;

    public IMongoDatabase Database { get; private set; } = null!;

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
        Database = new MongoClient(_runner.ConnectionString).GetDatabase("invoicing_test");
    }

    public Task DisposeAsync()
    {
        _runner?.Dispose();
        return Task.CompletedTask;
    }
}

/// <summary>A resolver that hands every client the one ephemeral database — fine for tests.</summary>
internal sealed class FixedDatabaseResolver(IMongoDatabase database) : IInvoicingDatabaseResolver
{
    public Task<IMongoDatabase> ResolveAsync(Guid clientId, CancellationToken cancellationToken = default) =>
        Task.FromResult(database);
}
