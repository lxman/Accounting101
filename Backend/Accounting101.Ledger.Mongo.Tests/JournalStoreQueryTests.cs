using Accounting101.Ledger.Core.Journal;
using MongoDB.Driver;

namespace Accounting101.Ledger.Mongo.Tests;

public sealed class JournalStoreQueryTests(MongoFixture fixture) : IClassFixture<MongoFixture>
{
    private static readonly Guid ClientId = Guid.NewGuid();

    private static AuditStamp Stamp() => new()
    {
        CreatedBy = Guid.NewGuid(),
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    private static JournalEntry MakePending(DateOnly date, long seq) =>
        new JournalEntryBuilder(
            id: Guid.NewGuid(),
            clientId: ClientId,
            sequenceNumber: seq,
            effectiveDate: date,
            postedAt: DateTimeOffset.UnixEpoch,
            audit: Stamp())
        .Debit(Guid.NewGuid(), 100m)
        .Credit(Guid.NewGuid(), 100m)
        .Build();  // Posting=PendingApproval, Status=Active by default

    private static JournalEntry MakePosted(DateOnly date, long seq)
    {
        JournalEntryBuilder b = new(
            id: Guid.NewGuid(),
            clientId: ClientId,
            sequenceNumber: seq,
            effectiveDate: date,
            postedAt: DateTimeOffset.UnixEpoch,
            audit: Stamp());
        b.Posting = PostingState.Posted;
        return b.Debit(Guid.NewGuid(), 100m).Credit(Guid.NewGuid(), 100m).Build();
    }

    private IMongoClient Client => fixture.Database.Client;

    private MongoJournalStore Journal => new(fixture.Database, "jq_" + Guid.NewGuid().ToString("N"));

    private MongoSequenceStore Sequences => new(fixture.Database, "sq_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task GetPendingThrough_returns_active_pending_entries_on_or_before_asOf_only()
    {
        // Arrange: one Active+PendingApproval @ 2024-06-30 (R1), one Posted @ 2024-06-30 (R2),
        // one Active+PendingApproval @ 2024-07-15 (future, R3), one Voided pending-shaped @ 2024-06-10 (R4).
        MongoJournalStore journal = new(fixture.Database, "jq_seed_" + Guid.NewGuid().ToString("N"));

        JournalEntry r1 = MakePending(new DateOnly(2024, 6, 30), 1);  // should be returned
        JournalEntry r2 = MakePosted(new DateOnly(2024, 6, 30), 2);    // Posted — excluded
        JournalEntry r3 = MakePending(new DateOnly(2024, 7, 15), 3);   // after asOf — excluded
        JournalEntry r4 = MakePending(new DateOnly(2024, 6, 10), 4).Void(); // Voided — excluded

        using IClientSessionHandle session = await Client.StartSessionAsync();
        await journal.AppendAsync(r1, session);
        await journal.AppendAsync(r2, session);
        await journal.AppendAsync(r3, session);
        // r4 is voided — append the original then replace with voided state
        JournalEntry r4Original = MakePending(new DateOnly(2024, 6, 10), 4);
        await journal.AppendAsync(r4Original, session);
        await journal.ReplaceAsync(r4Original.Void(), session);

        IReadOnlyList<JournalEntry> blockers =
            await journal.GetPendingThroughAsync(ClientId, new DateOnly(2024, 6, 30), session, CancellationToken.None);

        Assert.Equal(new[] { r1.Id }, blockers.Select(b => b.Id).ToArray());
    }

    [Fact]
    public async Task TouchJournal_does_not_consume_a_sequence_number()
    {
        MongoSequenceStore sequences = new(fixture.Database, "sq_touch_" + Guid.NewGuid().ToString("N"));
        using IClientSessionHandle session = await Client.StartSessionAsync();
        long before = await sequences.NextJournalAsync(ClientId, session, CancellationToken.None); // e.g. 1

        await sequences.TouchJournalAsync(ClientId, session, CancellationToken.None);

        long after = await sequences.NextJournalAsync(ClientId, session, CancellationToken.None);
        Assert.Equal(before + 1, after); // touch bumped `guard`, NOT `seq` — next seq is contiguous
    }
}
