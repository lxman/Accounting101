using EphemeralMongo;
using MongoDB.Driver;

namespace Accounting101.Ledger.Mongo.Tests;

/// <summary>
/// Spins up a disposable, self-contained MongoDB (via EphemeralMongo) as a
/// single-node replica set — so the suite needs no external Mongo, and transactions
/// (for the supersede/void lifecycle) are available. One instance is shared across
/// the test class; each test takes its own collection via <see cref="NewStore"/> for
/// isolation. The benchmark harness deliberately stays on a real/native Mongo.
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
            UseSingleNodeReplicaSet = true,          // enables transactions / change streams
            AdditionalArguments = ["--quiet"],
            StandardErrorLogger = Console.Error.WriteLine,
        };

        _runner = await MongoRunner.RunAsync(options);
        Database = new MongoClient(_runner.ConnectionString).GetDatabase("ledger_proto_test");
    }

    public Task DisposeAsync()
    {
        _runner?.Dispose();
        return Task.CompletedTask;
    }

    public MongoJournalStore NewStore() =>
        new(Database, "journal_" + Guid.NewGuid().ToString("N"));
}
