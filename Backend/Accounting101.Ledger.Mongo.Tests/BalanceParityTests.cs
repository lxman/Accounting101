using Accounting101.Ledger.Core.Journal;

namespace Accounting101.Ledger.Mongo.Tests;

public sealed class BalanceParityTests(MongoFixture fixture) : IClassFixture<MongoFixture>
{
    private static AuditStamp Stamp() => new()
    {
        CreatedBy = Guid.NewGuid(),
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    private static JournalEntryBuilder Builder(Guid clientId, long sequence, PostingState posting)
    {
        JournalEntryBuilder builder = new(
            id: Guid.NewGuid(),
            clientId: clientId,
            sequenceNumber: sequence,
            effectiveDate: new DateOnly(2026, 4, 1),
            postedAt: DateTimeOffset.UnixEpoch,
            audit: Stamp());
        builder.Posting = posting;
        return builder;
    }

    [Fact]
    public async Task Load_and_fold_matches_server_side_aggregation()
    {
        MongoJournalStore store = fixture.NewStore();
        Guid client = Guid.NewGuid();
        Guid cash = Guid.NewGuid();
        Guid revenue = Guid.NewGuid();
        Guid rent = Guid.NewGuid();

        await store.AppendAsync(Builder(client, 1, PostingState.Posted).Debit(cash, 100m).Credit(revenue, 100m).Build());
        await store.AppendAsync(Builder(client, 2, PostingState.Posted).Debit(rent, 30m).Credit(cash, 30m).Build());
        await store.AppendAsync(Builder(client, 3, PostingState.Posted).Debit(cash, 7.25m).Credit(revenue, 7.25m).Build());
        // Not on the books — must be excluded by BOTH read paths:
        await store.AppendAsync(Builder(client, 4, PostingState.PendingApproval).Debit(cash, 999m).Credit(revenue, 999m).Build());

        IReadOnlyList<JournalEntry> entries = await store.GetByClientAsync(client);
        IReadOnlyDictionary<Guid, decimal> folded = LedgerReplay.Balances(entries);
        IReadOnlyDictionary<Guid, decimal> aggregated = await store.AggregateBalancesAsync(client);

        // The two paths agree, account for account.
        Assert.Equal(folded.Count, aggregated.Count);
        foreach ((Guid account, decimal balance) in folded)
        {
            Assert.True(aggregated.TryGetValue(account, out decimal serverSide), $"aggregation missing account {account}");
            Assert.Equal(balance, serverSide);
        }

        // ...and the expected numbers, with the pending entry excluded.
        Assert.Equal(77.25m, aggregated[cash]);       // 100 - 30 + 7.25
        Assert.Equal(-107.25m, aggregated[revenue]);  // -100 - 7.25
        Assert.Equal(30m, aggregated[rent]);
    }
}
