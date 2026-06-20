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

    /// <summary>A builder for an entry that is already on the books (Active + Posted) — the fold's gate.</summary>
    private static JournalEntryBuilder Posted(Guid clientId, long sequence)
    {
        JournalEntryBuilder builder = Builder(clientId, sequence);
        builder.Posting = PostingState.Posted;
        return builder;
    }

    private static Dictionary<string, Guid> Customer(Guid id) => new() { ["Customer"] = id };

    [Fact]
    public async Task Entry_round_trips_with_amounts_and_balance_intact()
    {
        MongoJournalStore store = fixture.NewStore();
        var clientId = Guid.NewGuid();
        var cash = Guid.NewGuid();
        var revenue = Guid.NewGuid();
        var taxPayable = Guid.NewGuid();

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
        var clientId = Guid.NewGuid();
        var cash = Guid.NewGuid();
        var rent = Guid.NewGuid();
        var revenue = Guid.NewGuid();

        await store.AppendAsync(Builder(clientId, 1).Debit(cash, 100m).Credit(revenue, 100m).Build());
        await store.AppendAsync(Builder(clientId, 2).Debit(rent, 50m).Credit(cash, 50m).Build());

        IReadOnlyList<JournalEntry> touchingRent = await store.GetTouchingAccountAsync(clientId, rent);

        Assert.Single(touchingRent);
        Assert.Equal(2, touchingRent[0].SequenceNumber);
    }

    [Fact]
    public async Task Source_back_link_round_trips_and_resolves_an_entry_from_its_document()
    {
        MongoJournalStore store = fixture.NewStore();
        var clientId = Guid.NewGuid();
        var ar = Guid.NewGuid();
        var revenue = Guid.NewGuid();
        var cash = Guid.NewGuid();
        var invoice = Guid.NewGuid();

        // The invoice posts an entry, then a reversal — both carry the same back-link.
        JournalEntryBuilder issued = Builder(clientId, 1);
        issued.SourceRef = invoice;
        issued.SourceType = "Invoice";
        await store.AppendAsync(issued.Debit(ar, 100m).Credit(revenue, 100m).Build());

        JournalEntryBuilder reversed = Builder(clientId, 2);
        reversed.SourceRef = invoice;
        reversed.SourceType = "Invoice";
        await store.AppendAsync(reversed.Credit(ar, 100m).Debit(revenue, 100m).Build());

        // An unrelated entry with no source document must not come back.
        await store.AppendAsync(Builder(clientId, 3).Debit(cash, 50m).Credit(revenue, 50m).Build());

        IReadOnlyList<JournalEntry> fromInvoice = await store.GetBySourceRefAsync(clientId, invoice);

        Assert.Equal(2, fromInvoice.Count);
        Assert.All(fromInvoice, e => Assert.Equal(invoice, e.SourceRef));
        Assert.All(fromInvoice, e => Assert.Equal("Invoice", e.SourceType));
    }

    [Fact]
    public async Task Subledger_breaks_a_control_account_out_by_customer_and_ties_to_its_balance()
    {
        MongoJournalStore store = fixture.NewStore();
        var clientId = Guid.NewGuid();
        var ar = Guid.NewGuid();
        var revenue = Guid.NewGuid();
        var custA = Guid.NewGuid();
        var custB = Guid.NewGuid();

        await store.AppendAsync(Posted(clientId, 1).Debit(ar, 100m, Customer(custA)).Credit(revenue, 100m).Build());
        await store.AppendAsync(Posted(clientId, 2).Debit(ar, 60m, Customer(custB)).Credit(revenue, 60m).Build());
        await store.AppendAsync(Posted(clientId, 3).Debit(ar, 40m, Customer(custA)).Credit(revenue, 40m).Build());

        IReadOnlyList<SubledgerBalance> subledger =
            await store.AggregateSubledgerAsync(clientId, "Customer", accountId: ar);

        Assert.Equal(140m, subledger.Single(s => s.DimensionValue == custA).Balance); // 100 + 40
        Assert.Equal(60m, subledger.Single(s => s.DimensionValue == custB).Balance);
        Assert.All(subledger, s => Assert.Equal(ar, s.AccountId));

        // The subledger is the same lines grouped finer, so it sums to the control-account balance.
        IReadOnlyDictionary<Guid, decimal> trialBalance = await store.AggregateBalancesAsync(clientId);
        Assert.Equal(trialBalance[ar], subledger.Sum(s => s.Balance));
    }

    [Fact]
    public async Task Dimension_detail_returns_only_entries_touching_that_customer()
    {
        MongoJournalStore store = fixture.NewStore();
        var clientId = Guid.NewGuid();
        var ar = Guid.NewGuid();
        var revenue = Guid.NewGuid();
        var custA = Guid.NewGuid();
        var custB = Guid.NewGuid();

        await store.AppendAsync(Posted(clientId, 1).Debit(ar, 100m, Customer(custA)).Credit(revenue, 100m).Build());
        await store.AppendAsync(Posted(clientId, 2).Debit(ar, 60m, Customer(custB)).Credit(revenue, 60m).Build());
        await store.AppendAsync(Posted(clientId, 3).Debit(ar, 40m, Customer(custA)).Credit(revenue, 40m).Build());

        IReadOnlyList<JournalEntry> forCustA =
            await store.GetTouchingDimensionAsync(clientId, "Customer", custA);

        Assert.Equal([1, 3], forCustA.Select(e => e.SequenceNumber).OrderBy(n => n));
    }

    [Fact]
    public async Task Unique_sequence_index_rejects_a_duplicate_sequence_for_a_client()
    {
        MongoJournalStore store = fixture.NewStore();
        await store.EnsureIndexesAsync();

        var clientId = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        await store.AppendAsync(Builder(clientId, 1).Debit(a, 10m).Credit(b, 10m).Build());

        await Assert.ThrowsAsync<MongoWriteException>(() =>
            store.AppendAsync(Builder(clientId, 1).Debit(a, 20m).Credit(b, 20m).Build()));
    }
}
