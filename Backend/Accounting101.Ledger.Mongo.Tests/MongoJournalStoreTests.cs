using Accounting101.Ledger.Core.Journal;
using MongoDB.Driver;

namespace Accounting101.Ledger.Mongo.Tests;

public sealed class MongoJournalStoreTests(MongoFixture fixture) : IClassFixture<MongoFixture>
{
    private static AuditStamp Stamp() => new()
    {
        CreatedBy = Guid.NewGuid(),
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    private static JournalEntryBuilder Builder(Guid clientId, long sequence) => new(
        id: Guid.NewGuid(),
        clientId: clientId,
        sequenceNumber: sequence,
        effectiveDate: new DateOnly(2026, 3, 15),
        postedAt: DateTimeOffset.UnixEpoch,
        audit: Stamp());

    [Fact]
    public async Task Entry_round_trips_with_amounts_and_balance_intact()
    {
        MongoJournalStore store = fixture.NewStore();
        Guid clientId = Guid.NewGuid();
        Guid cash = Guid.NewGuid();
        Guid revenue = Guid.NewGuid();
        Guid taxPayable = Guid.NewGuid();

        JournalEntryBuilder builder = Builder(clientId, 1);
        builder.Memo = "tax sale";
        builder.Posting = PostingState.Posted;
        JournalEntry original = builder
            .Debit(cash, 107.50m)
            .Credit(revenue, 100m)
            .Credit(taxPayable, 7.50m)
            .Build();

        await store.AppendAsync(original);
        JournalEntry? read = await store.GetAsync(original.Id);

        Assert.NotNull(read);
        Assert.Equal(original.Id, read!.Id);
        Assert.Equal(clientId, read.ClientId);
        Assert.Equal(1, read.SequenceNumber);
        Assert.Equal(new DateOnly(2026, 3, 15), read.EffectiveDate);
        Assert.Equal(PostingState.Posted, read.Posting);
        Assert.Equal(LifecycleStatus.Active, read.Status);
        Assert.Equal("tax sale", read.Memo);
        Assert.Equal(3, read.Lines.Count);
        Assert.Equal(0m, read.SignedTotal());            // balance survived the round-trip
        Assert.Equal(107.50m, read.BalanceFor(cash));     // Decimal128 fidelity
        Assert.Equal(-100m, read.BalanceFor(revenue));
        Assert.Equal(-7.50m, read.BalanceFor(taxPayable));
    }

    [Fact]
    public async Task Query_by_account_returns_only_entries_touching_that_account()
    {
        MongoJournalStore store = fixture.NewStore();
        Guid clientId = Guid.NewGuid();
        Guid cash = Guid.NewGuid();
        Guid rent = Guid.NewGuid();
        Guid revenue = Guid.NewGuid();

        await store.AppendAsync(Builder(clientId, 1).Debit(cash, 100m).Credit(revenue, 100m).Build());
        await store.AppendAsync(Builder(clientId, 2).Debit(rent, 50m).Credit(cash, 50m).Build());

        IReadOnlyList<JournalEntry> touchingRent = await store.GetTouchingAccountAsync(clientId, rent);

        Assert.Single(touchingRent);
        Assert.Equal(2, touchingRent[0].SequenceNumber);
    }

    [Fact]
    public async Task Unique_sequence_index_rejects_a_duplicate_sequence_for_a_client()
    {
        MongoJournalStore store = fixture.NewStore();
        await store.EnsureIndexesAsync();

        Guid clientId = Guid.NewGuid();
        Guid a = Guid.NewGuid();
        Guid b = Guid.NewGuid();

        await store.AppendAsync(Builder(clientId, 1).Debit(a, 10m).Credit(b, 10m).Build());

        await Assert.ThrowsAsync<MongoWriteException>(() =>
            store.AppendAsync(Builder(clientId, 1).Debit(a, 20m).Credit(b, 20m).Build()));
    }
}
