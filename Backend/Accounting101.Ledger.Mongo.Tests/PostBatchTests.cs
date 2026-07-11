using Accounting101.Ledger.Core.Journal;
using MongoDB.Driver;

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
        MakeBalancedEntry(Guid.NewGuid(), clientId, date, sequenceNumber: 0);

    private static JournalEntry MakeBalancedEntry(Guid id, Guid clientId, DateOnly date, long sequenceNumber) =>
        JournalEntry.Create(
            id: id,
            clientId: clientId,
            sequenceNumber: sequenceNumber,
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

    /// <summary>
    /// This is a fast-fail-refusal test, NOT a rollback test: <see cref="LedgerService.PostBatchAsync"/> runs
    /// <c>EnsureOpenAsync</c> over every entry BEFORE <c>InTransactionAsync</c> opens, so the closed-period
    /// entry is caught pre-transaction and neither entry is ever written. It would pass even if the
    /// transaction wrapping the append loop were deleted entirely, so it proves nothing about atomicity.
    /// See <see cref="PostBatch_rolls_back_the_already_appended_entry_when_a_later_entry_fails_inside_the_transaction"/>
    /// for the actual mid-transaction rollback proof.
    /// </summary>
    [Fact]
    public async Task PostBatch_refuses_when_an_entry_targets_a_closed_period_before_writing()
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

        Assert.Null(await journal.GetAsync(ok.Id));
        Assert.Null(await journal.GetAsync(bad.Id));
    }

    /// <summary>
    /// The real atomicity proof: both entries share the same OPEN effective date, so both clear the
    /// pre-transaction <c>EnsureOpenAsync</c> fast-fail loop and the batch enters <c>InTransactionAsync</c>.
    /// Inside the transaction the FIRST entry is appended successfully (a fresh, otherwise-valid id). The
    /// SECOND entry reuses the <c>_id</c> of an entry that was already durably committed outside the batch,
    /// so its <c>AppendAsync</c> hits a duplicate-key conflict mid-transaction — after the first entry's
    /// insert already landed in the (uncommitted) transaction. If the transaction actually rolls back on
    /// failure, the first entry's id must be absent from the journal afterward; if it were present, the
    /// transaction boundary would be provably broken.
    /// </summary>
    [Fact]
    public async Task PostBatch_rolls_back_the_already_appended_entry_when_a_later_entry_fails_inside_the_transaction()
    {
        (LedgerService service, MongoJournalStore journal) = NewLedger();
        Guid clientId = Guid.NewGuid();
        DateOnly date = new(2026, 6, 1);
        Actor actor = User();

        // Pre-insert a committed entry directly (outside the batch, outside any transaction). Its id is
        // the collision target for the second batch entry.
        Guid collidingId = Guid.NewGuid();
        JournalEntry preExisting = MakeBalancedEntry(collidingId, clientId, date, sequenceNumber: 9999);
        await journal.AppendAsync(preExisting);

        JournalEntry first = MakeBalancedEntry(clientId, date);                         // fresh id — would succeed alone
        JournalEntry second = MakeBalancedEntry(collidingId, clientId, date, sequenceNumber: 0); // duplicate _id — fails inside the transaction

        await Assert.ThrowsAsync<MongoWriteException>(() => service.PostBatchAsync([first, second], actor));

        Assert.Null(await journal.GetAsync(first.Id)); // proof: the already-appended first entry was rolled back
    }

    [Fact]
    public async Task PostBatch_throws_ArgumentException_for_an_empty_batch()
    {
        (LedgerService service, _) = NewLedger();
        Actor actor = User();

        await Assert.ThrowsAsync<ArgumentException>(() => service.PostBatchAsync([], actor));
    }
}
