using Accounting101.TestSupport;
using EphemeralMongo;
using MongoDB.Driver;

namespace Accounting101.Ledger.Mongo.Tests;

/// <summary>
/// Connects to the shared EphemeralMongo instance (single-node replica set)
/// so the suite needs no external Mongo, and transactions (for the
/// supersede/void lifecycle) are available. One instance is shared across
/// the test class; each test takes its own collection via <see cref="NewStore"/>
/// for isolation. The benchmark harness deliberately stays on a real/native Mongo.
/// </summary>
public sealed class MongoFixture : IAsyncLifetime
{
    public IMongoDatabase Database { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        IMongoRunner runner = await SharedMongo.InstanceAsync();
        Database = new MongoClient(runner.ConnectionString).GetDatabase("ledger_proto_test");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public MongoJournalStore NewStore() =>
        new(Database, "journal_" + Guid.NewGuid().ToString("N"));
}
