using Accounting101.Ledger.Core.Journal;

namespace Accounting101.Ledger.Mongo.Tests;

public sealed class PostBatchTests(MongoFixture fixture) : IClassFixture<MongoFixture>
{
    private static AuditStamp Stamp() => new()
    {
        CreatedBy = Guid.NewGuid(),
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    private static Actor User() => new()
    {
        UserId = Guid.NewGuid(),
        Name = "tester",
        Claims = [new Claim("role", "bookkeeper")],
    };

    private (LedgerService service, MongoJournalStore store) NewLedger()
    {
        MongoJournalStore store = fixture.NewStore();
        MongoBalanceProjection projection = new(fixture.Database, store, "balances_" + Guid.NewGuid().ToString("N"));
        MongoCheckpointStore checkpoints = new(fixture.Database, "checkpoints_" + Guid.NewGuid().ToString("N"));
        MongoAuditLog audit = new(fixture.Database, "audit_" + Guid.NewGuid().ToString("N"));
        return (new LedgerService(fixture.Database.Client, store, projection, checkpoints, audit, new MongoSequenceStore(fixture.Database)), store);
    }

    private static JournalEntry MakeBalancedEntry(Guid clientId, DateOnly date) =>
        JournalEntry.Create(
            id: Guid.NewGuid(),
            clientId: clientId,
            sequenceNumber: 0,
            effectiveDate: date,
            postedAt: DateTimeOffset.UnixEpoch,
            type: EntryType.Standard,
            audit: Stamp(),
            lines:
            [
                new Line { Id = Guid.NewGuid(), AccountId = Guid.NewGuid(), Direction = Direction.Debit, Amount = 10m },
                new Line { Id = Guid.NewGuid(), AccountId = Guid.NewGuid(), Direction = Direction.Credit, Amount = 10m },
            ]);

    [Fact]
    public async Task PostBatch_writes_all_entries_with_consecutive_sequence_numbers()
    {
        (LedgerService service, MongoJournalStore journal) = NewLedger();
        Guid clientId = Guid.NewGuid();
        DateOnly date = new(2026, 6, 1);
        Actor actor = User();

        JournalEntry a = MakeBalancedEntry(clientId, date);
        JournalEntry b = MakeBalancedEntry(clientId, date);

        IReadOnlyList<JournalEntry> written = await service.PostBatchAsync([a, b], actor);

        Assert.Equal(2, written.Count);
        Assert.Equal(written[0].SequenceNumber + 1, written[1].SequenceNumber); // gapless, consecutive
        Assert.NotNull(await journal.GetAsync(a.Id));
        Assert.NotNull(await journal.GetAsync(b.Id));
    }

    [Fact]
    public async Task PostBatch_rolls_back_entirely_when_one_entry_lands_in_a_closed_period()
    {
        (LedgerService service, MongoJournalStore journal) = NewLedger();
        Guid clientId = Guid.NewGuid();
        DateOnly date = new(2026, 6, 1);
        Actor actor = User();

        // Close through `date`. A batch [openDatedEntry, closedDatedEntry] must throw and write NEITHER.
        await service.CloseAsync(clientId, date, actor);
        JournalEntry ok = MakeBalancedEntry(clientId, date.AddDays(1));
        JournalEntry bad = MakeBalancedEntry(clientId, date); // in the frozen period

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PostBatchAsync([ok, bad], actor));

        Assert.Null(await journal.GetAsync(ok.Id));   // rolled back with the bad one
        Assert.Null(await journal.GetAsync(bad.Id));
    }
}
