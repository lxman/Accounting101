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
    public async Task GetByReference_returns_only_matching_reference()
    {
        // Arrange: three entries — two with Reference "R1", one with "R2".
        // A second client also has a "R1" entry (must not leak across clients).
        MongoJournalStore journal = new(fixture.Database, "jq_ref_" + Guid.NewGuid().ToString("N"));
        Guid otherClient = Guid.NewGuid();

        JournalEntryBuilder b1 = new(Guid.NewGuid(), ClientId, 1, new DateOnly(2024, 1, 1), DateTimeOffset.UnixEpoch, Stamp());
        b1.Reference = "R1";
        JournalEntry e1 = b1.Debit(Guid.NewGuid(), 100m).Credit(Guid.NewGuid(), 100m).Build();

        JournalEntryBuilder b2 = new(Guid.NewGuid(), ClientId, 2, new DateOnly(2024, 1, 2), DateTimeOffset.UnixEpoch, Stamp());
        b2.Reference = "R1";
        JournalEntry e2 = b2.Debit(Guid.NewGuid(), 100m).Credit(Guid.NewGuid(), 100m).Build();

        JournalEntryBuilder b3 = new(Guid.NewGuid(), ClientId, 3, new DateOnly(2024, 1, 3), DateTimeOffset.UnixEpoch, Stamp());
        b3.Reference = "R2";
        JournalEntry e3 = b3.Debit(Guid.NewGuid(), 100m).Credit(Guid.NewGuid(), 100m).Build();

        JournalEntryBuilder b4 = new(Guid.NewGuid(), otherClient, 1, new DateOnly(2024, 1, 1), DateTimeOffset.UnixEpoch, Stamp());
        b4.Reference = "R1";
        JournalEntry e4 = b4.Debit(Guid.NewGuid(), 100m).Credit(Guid.NewGuid(), 100m).Build();

        using IClientSessionHandle session = await Client.StartSessionAsync();
        await journal.AppendAsync(e1, session);
        await journal.AppendAsync(e2, session);
        await journal.AppendAsync(e3, session);
        await journal.AppendAsync(e4, session);

        // Act
        IReadOnlyList<JournalEntry> r1Results = await journal.GetByReferenceAsync(ClientId, "R1", CancellationToken.None);
        IReadOnlyList<JournalEntry> rxResults = await journal.GetByReferenceAsync(ClientId, "RX", CancellationToken.None);

        // Assert: "R1" returns the two matching entries in sequence order; "RX" returns empty
        Assert.Equal(new[] { e1.Id, e2.Id }, r1Results.Select(e => e.Id).ToArray());
        Assert.Empty(rxResults);
    }

    [Fact]
    public async Task GetByPosting_returns_only_that_posting_state_paged()
    {
        // Arrange: seed 3 PendingApproval entries and 2 Posted entries.
        MongoJournalStore journal = new(fixture.Database, "jq_post_" + Guid.NewGuid().ToString("N"));

        JournalEntry p1 = MakePending(new DateOnly(2024, 1, 1), 1);
        JournalEntry p2 = MakePending(new DateOnly(2024, 1, 2), 2);
        JournalEntry p3 = MakePending(new DateOnly(2024, 1, 3), 3);
        JournalEntry posted1 = MakePosted(new DateOnly(2024, 1, 4), 4);
        JournalEntry posted2 = MakePosted(new DateOnly(2024, 1, 5), 5);

        using IClientSessionHandle session = await Client.StartSessionAsync();
        await journal.AppendAsync(p1, session);
        await journal.AppendAsync(p2, session);
        await journal.AppendAsync(p3, session);
        await journal.AppendAsync(posted1, session);
        await journal.AppendAsync(posted2, session);

        // Act
        IReadOnlyList<JournalEntry> pending = await journal.GetByPostingAsync(
            ClientId, PostingState.PendingApproval, 0, 200, CancellationToken.None);
        IReadOnlyList<JournalEntry> posted = await journal.GetByPostingAsync(
            ClientId, PostingState.Posted, 0, 200, CancellationToken.None);

        // Assert: only the pending entries returned for PendingApproval, sequence-ordered
        Assert.Equal(new[] { p1.Id, p2.Id, p3.Id }, pending.Select(e => e.Id).ToArray());
        Assert.Equal(new[] { posted1.Id, posted2.Id }, posted.Select(e => e.Id).ToArray());
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
