using Accounting101.Ledger.Core.Journal;
using Accounting101.Ledger.Mongo.Documents;

namespace Accounting101.Ledger.Mongo.Tests;

/// <summary>
/// Concurrency guards under real concurrent writers: optimistic concurrency lets only one of many
/// racing approvals win (so the projection is never double-applied), and the unique audit-sequence
/// index plus append-retry keeps the hash chain a single gapless, verifiable sequence.
/// </summary>
public sealed class ConcurrencyTests(MongoFixture fixture) : IClassFixture<MongoFixture>
{
    private static AuditStamp Stamp() => new() { CreatedBy = Guid.NewGuid(), CreatedAt = DateTimeOffset.UnixEpoch };

    private static Actor User() => new() { UserId = Guid.NewGuid(), Name = "tester", Claims = [new Claim("role", "controller")] };

    private (LedgerService service, MongoJournalStore store, MongoBalanceProjection projection) NewLedger()
    {
        MongoJournalStore store = fixture.NewStore();
        MongoBalanceProjection projection = new(fixture.Database, store, "balances_" + Guid.NewGuid().ToString("N"));
        MongoCheckpointStore checkpoints = new(fixture.Database, "checkpoints_" + Guid.NewGuid().ToString("N"));
        MongoAuditLog audit = new(fixture.Database, "audit_" + Guid.NewGuid().ToString("N"));
        return (new LedgerService(store, projection, checkpoints, audit), store, projection);
    }

    private static JournalEntry Entry(Guid clientId, Guid debit, Guid credit, decimal amount) =>
        JournalEntry.Create(
            id: Guid.NewGuid(),
            clientId: clientId,
            sequenceNumber: 1,
            effectiveDate: new DateOnly(2026, 6, 1),
            postedAt: DateTimeOffset.UnixEpoch,
            type: EntryType.Standard,
            audit: Stamp(),
            lines:
            [
                new Line { Id = Guid.NewGuid(), AccountId = debit, Direction = Direction.Debit, Amount = amount },
                new Line { Id = Guid.NewGuid(), AccountId = credit, Direction = Direction.Credit, Amount = amount },
            ]);

    [Fact]
    public async Task Only_one_of_many_concurrent_approvals_wins()
    {
        (LedgerService service, MongoJournalStore store, MongoBalanceProjection projection) = NewLedger();
        var client = Guid.NewGuid();
        var cash = Guid.NewGuid();
        var revenue = Guid.NewGuid();

        JournalEntry entry = Entry(client, cash, revenue, 100m);
        await service.PostAsync(entry, User());

        // Eight threads race to approve the same pending entry.
        Task<bool>[] attempts = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(async () =>
            {
                try
                {
                    await service.ApproveAsync(entry.Id, User());
                    return true;
                }
                catch (InvalidOperationException) // ConcurrencyConflict, or "already posted" for the stragglers
                {
                    return false;
                }
            }))
            .ToArray();

        bool[] results = await Task.WhenAll(attempts);

        Assert.Equal(1, results.Count(won => won));                 // exactly one approval took effect
        IReadOnlyDictionary<Guid, decimal> balances = await projection.GetTrialBalanceAsync(client);
        Assert.Equal(100m, balances[cash]);                          // applied exactly once — never double-counted
        Assert.Equal(PostingState.Posted, (await store.GetAsync(entry.Id))!.Posting);
    }

    [Fact]
    public async Task Concurrent_audit_appends_keep_one_gapless_verifiable_chain()
    {
        MongoAuditLog audit = new(fixture.Database, "audit_" + Guid.NewGuid().ToString("N"));
        await audit.EnsureIndexesAsync();
        var client = Guid.NewGuid();

        const int n = 12;
        Task[] appends = Enumerable.Range(0, n)
            .Select(i => Task.Run(() =>
                audit.AppendAsync(client, Guid.NewGuid(), 1, AuditAction.Created, User(), $"r{i}", DateTimeOffset.UnixEpoch)))
            .ToArray();

        await Task.WhenAll(appends);

        IReadOnlyList<AuditRecordDocument> records = await audit.GetForClientAsync(client);
        Assert.Equal(n, records.Count);
        Assert.Equal(Enumerable.Range(1, n).Select(i => (long)i), records.Select(r => r.Sequence)); // gapless, no fork
        Assert.True(await audit.VerifyAsync(client));                                                // chain intact
    }
}
