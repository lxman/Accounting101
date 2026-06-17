using Accounting101.Ledger.Core.Journal;

namespace Accounting101.Ledger.Mongo.Tests;

public sealed class BalanceProjectionTests(MongoFixture fixture) : IClassFixture<MongoFixture>
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
            effectiveDate: new DateOnly(2026, 5, 1),
            postedAt: DateTimeOffset.UnixEpoch,
            audit: Stamp());
        builder.Posting = posting;
        return builder;
    }

    private MongoBalanceProjection NewProjection(MongoJournalStore store) =>
        new(fixture.Database, store, "balances_" + Guid.NewGuid().ToString("N"));

    private static async Task PostAsync(MongoJournalStore store, MongoBalanceProjection projection, JournalEntry entry)
    {
        await store.AppendAsync(entry);
        await projection.ApplyAsync(entry);
    }

    [Fact]
    public async Task Projection_matches_the_aggregation_and_the_fold()
    {
        MongoJournalStore store = fixture.NewStore();
        MongoBalanceProjection projection = NewProjection(store);
        var client = Guid.NewGuid();
        var cash = Guid.NewGuid();
        var revenue = Guid.NewGuid();
        var rent = Guid.NewGuid();

        await PostAsync(store, projection, Builder(client, 1, PostingState.Posted).Debit(cash, 100m).Credit(revenue, 100m).Build());
        await PostAsync(store, projection, Builder(client, 2, PostingState.Posted).Debit(rent, 30m).Credit(cash, 30m).Build());

        IReadOnlyDictionary<Guid, decimal> projected = await projection.GetTrialBalanceAsync(client);
        IReadOnlyDictionary<Guid, decimal> aggregated = await store.AggregateBalancesAsync(client);
        IReadOnlyDictionary<Guid, decimal> folded = LedgerReplay.Balances(await store.GetByClientAsync(client));

        Assert.Equal(70m, projected[cash]);       // 100 - 30
        Assert.Equal(-100m, projected[revenue]);
        Assert.Equal(30m, projected[rent]);

        // All three read paths agree, account for account.
        Assert.Equal(aggregated.Count, projected.Count);
        foreach ((Guid account, decimal balance) in aggregated)
        {
            Assert.Equal(balance, projected[account]);
            Assert.Equal(balance, folded[account]);
        }
    }

    [Fact]
    public async Task Pending_entries_do_not_affect_the_projection()
    {
        MongoJournalStore store = fixture.NewStore();
        MongoBalanceProjection projection = NewProjection(store);
        var client = Guid.NewGuid();
        var cash = Guid.NewGuid();
        var revenue = Guid.NewGuid();

        await PostAsync(store, projection, Builder(client, 1, PostingState.Posted).Debit(cash, 100m).Credit(revenue, 100m).Build());
        await PostAsync(store, projection, Builder(client, 2, PostingState.PendingApproval).Debit(cash, 999m).Credit(revenue, 999m).Build());

        IReadOnlyDictionary<Guid, decimal> projected = await projection.GetTrialBalanceAsync(client);

        Assert.Equal(100m, projected[cash]); // the pending entry is not on the books
    }

    [Fact]
    public async Task Compound_entry_nets_repeated_accounts_into_one_increment()
    {
        MongoJournalStore store = fixture.NewStore();
        MongoBalanceProjection projection = NewProjection(store);
        var client = Guid.NewGuid();
        var cash = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        // Two debit lines to the same account in one entry; credits split across two.
        await PostAsync(store, projection, Builder(client, 1, PostingState.Posted)
            .Debit(cash, 60m).Debit(cash, 40m).Credit(a, 70m).Credit(b, 30m).Build());

        IReadOnlyDictionary<Guid, decimal> projected = await projection.GetTrialBalanceAsync(client);

        Assert.Equal(100m, projected[cash]); // 60 + 40 netted
        Assert.Equal(-70m, projected[a]);
        Assert.Equal(-30m, projected[b]);
    }

    [Fact]
    public async Task Rebuild_reproduces_the_projection_from_the_journal()
    {
        MongoJournalStore store = fixture.NewStore();
        MongoBalanceProjection projection = NewProjection(store);
        var client = Guid.NewGuid();
        var cash = Guid.NewGuid();
        var revenue = Guid.NewGuid();

        // Append posted entries WITHOUT applying — simulate a lost/stale projection.
        await store.AppendAsync(Builder(client, 1, PostingState.Posted).Debit(cash, 100m).Credit(revenue, 100m).Build());
        await store.AppendAsync(Builder(client, 2, PostingState.Posted).Debit(cash, 25m).Credit(revenue, 25m).Build());

        Assert.Empty(await projection.GetTrialBalanceAsync(client));

        await projection.RebuildAsync(client);

        IReadOnlyDictionary<Guid, decimal> projected = await projection.GetTrialBalanceAsync(client);
        Assert.Equal(125m, projected[cash]);
        Assert.Equal(-125m, projected[revenue]);
    }
}
