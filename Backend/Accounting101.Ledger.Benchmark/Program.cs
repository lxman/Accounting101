using System.Diagnostics;
using Accounting101.Ledger.Core.Journal;
using Accounting101.Ledger.Mongo;
using MongoDB.Driver;

// Persistence prototype benchmark — runs against a real/native Mongo (NOT a container,
// so the numbers are representative). Seeds throwaway data and drops its DB at the end.
//   override the target with:  LEDGER_MONGO=mongodb://host:port

string connectionString = Environment.GetEnvironmentVariable("LEDGER_MONGO") ?? "mongodb://localhost:27017";
LedgerMongoBootstrap.RegisterOnce();

MongoClient client = new(connectionString);
string databaseName = "ledger_bench_" + Guid.NewGuid().ToString("N");
IMongoDatabase database = client.GetDatabase(databaseName);

Console.WriteLine("Ledger persistence benchmark");
Console.WriteLine($"  mongo : {connectionString}");
Console.WriteLine($"  db    : {databaseName}");
Console.WriteLine();

try
{
    await MeasureAppendLatency(database, count: 2_000, accountCount: 25);

    Console.WriteLine();
    Console.WriteLine("trial-balance read paths (avg of 5 runs):");
    Console.WriteLine($"  {"entries",8} | {"load+fold",13} | {"server-agg",13} | {"projection",13}");
    Console.WriteLine($"  {new string('-', 8)}-+-{new string('-', 13)}-+-{new string('-', 13)}-+-{new string('-', 13)}");
    foreach (int n in new[] { 1_000, 10_000, 50_000 })
        await CompareReadPaths(database, entryCount: n, accountCount: 25, runs: 5);
}
finally
{
    await client.DropDatabaseAsync(databaseName);
    Console.WriteLine();
    Console.WriteLine($"Dropped {databaseName}");
}

return;

// ---------------- helpers ----------------

static Guid[] CreateAccounts(int count) =>
    Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();

static JournalEntry BuildEntry(Guid clientId, long sequence, Guid[] accounts, int i)
{
    Guid debit = accounts[i % accounts.Length];
    Guid credit = accounts[(i + 1) % accounts.Length];
    decimal amount = ((i % 1000) + 1) * 1.25m;

    JournalEntryBuilder builder = new(
        id: Guid.NewGuid(),
        clientId: clientId,
        sequenceNumber: sequence,
        effectiveDate: new DateOnly(2026, 1, 1),
        postedAt: DateTimeOffset.UnixEpoch,
        audit: new AuditStamp { CreatedBy = Guid.NewGuid(), CreatedAt = DateTimeOffset.UnixEpoch })
    {
        Posting = PostingState.Posted,
    };

    return builder.Debit(debit, amount).Credit(credit, amount).Build();
}

static async Task MeasureAppendLatency(IMongoDatabase database, int count, int accountCount)
{
    MongoJournalStore store = new(database, "append_" + Guid.NewGuid().ToString("N"));
    await store.EnsureIndexesAsync();
    Guid clientId = Guid.NewGuid();
    Guid[] accounts = CreateAccounts(accountCount);

    Stopwatch sw = Stopwatch.StartNew();
    for (int i = 0; i < count; i++)
        await store.AppendAsync(BuildEntry(clientId, i + 1, accounts, i));
    sw.Stop();

    double perInsert = sw.Elapsed.TotalMilliseconds / count;
    double perSecond = count / sw.Elapsed.TotalSeconds;
    Console.WriteLine(
        $"write-through append: {count:N0} inserts in {sw.ElapsedMilliseconds:N0} ms " +
        $"({perInsert:F3} ms/insert, {perSecond:N0} inserts/sec)");
}

static async Task CompareReadPaths(IMongoDatabase database, int entryCount, int accountCount, int runs)
{
    string collection = "read_" + Guid.NewGuid().ToString("N");
    MongoJournalStore store = new(database, collection);
    await store.EnsureIndexesAsync();
    Guid clientId = Guid.NewGuid();
    Guid[] accounts = CreateAccounts(accountCount);

    // Bulk-seed (setup, not measured) straight to the collection.
    IMongoCollection<JournalEntryDocument> raw = database.GetCollection<JournalEntryDocument>(collection);
    const int batchSize = 5_000;
    List<JournalEntryDocument> buffer = new(batchSize);
    for (int i = 0; i < entryCount; i++)
    {
        buffer.Add(JournalEntryDocument.FromDomain(BuildEntry(clientId, i + 1, accounts, i)));
        if (buffer.Count == batchSize)
        {
            await raw.InsertManyAsync(buffer);
            buffer.Clear();
        }
    }

    if (buffer.Count > 0)
        await raw.InsertManyAsync(buffer);

    // Warm up both paths (JIT, connection pool, cache).
    _ = LedgerReplay.Balances(await store.GetByClientAsync(clientId));
    _ = await store.AggregateBalancesAsync(clientId);

    double foldMs = await TimeAverage(runs, async () =>
    {
        _ = LedgerReplay.Balances(await store.GetByClientAsync(clientId));
    });

    double aggregateMs = await TimeAverage(runs, async () =>
    {
        _ = await store.AggregateBalancesAsync(clientId);
    });

    // Maintained projection: populate once (one aggregation), then O(1) point reads.
    MongoBalanceProjection projection = new(database, store, "proj_" + Guid.NewGuid().ToString("N"));
    await projection.RebuildAsync(clientId);
    double projectionMs = await TimeAverage(runs, async () =>
    {
        _ = await projection.GetTrialBalanceAsync(clientId);
    });

    Console.WriteLine($"  {entryCount,8:N0} | {foldMs,10:F1} ms | {aggregateMs,10:F1} ms | {projectionMs,10:F2} ms");
}

static async Task<double> TimeAverage(int runs, Func<Task> action)
{
    Stopwatch sw = Stopwatch.StartNew();
    for (int r = 0; r < runs; r++)
        await action();
    sw.Stop();
    return sw.Elapsed.TotalMilliseconds / runs;
}
