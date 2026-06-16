using Accounting101.Ledger.Core.Journal;

namespace Accounting101.Ledger.Mongo.Tests;

public sealed class LedgerServiceTests(MongoFixture fixture) : IClassFixture<MongoFixture>
{
    private static AuditStamp Stamp() => new()
    {
        CreatedBy = Guid.NewGuid(),
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    private (LedgerService service, MongoJournalStore store, MongoBalanceProjection projection) NewLedger()
    {
        MongoJournalStore store = fixture.NewStore();
        MongoBalanceProjection projection = new(fixture.Database, store, "balances_" + Guid.NewGuid().ToString("N"));
        return (new LedgerService(store, projection), store, projection);
    }

    private static JournalEntry Entry(
        Guid clientId, long sequence, Guid debit, Guid credit, decimal amount,
        PostingState posting = PostingState.PendingApproval, Guid? supersedes = null) =>
        JournalEntry.Create(
            id: Guid.NewGuid(),
            clientId: clientId,
            sequenceNumber: sequence,
            effectiveDate: new DateOnly(2026, 6, 1),
            postedAt: DateTimeOffset.UnixEpoch,
            type: EntryType.Standard,
            audit: Stamp(),
            lines:
            [
                new Line { Id = Guid.NewGuid(), AccountId = debit, Direction = Direction.Debit, Amount = amount },
                new Line { Id = Guid.NewGuid(), AccountId = credit, Direction = Direction.Credit, Amount = amount },
            ],
            posting: posting,
            supersedes: supersedes);

    [Fact]
    public async Task Approval_puts_an_entry_on_the_books()
    {
        (LedgerService service, MongoJournalStore store, MongoBalanceProjection projection) = NewLedger();
        Guid client = Guid.NewGuid();
        Guid cash = Guid.NewGuid();
        Guid revenue = Guid.NewGuid();

        JournalEntry entry = Entry(client, 1, cash, revenue, 100m);
        await service.PostAsync(entry);

        Assert.Empty(await projection.GetTrialBalanceAsync(client)); // pending: not on the books

        await service.ApproveAsync(entry.Id, Guid.NewGuid());

        IReadOnlyDictionary<Guid, decimal> balances = await projection.GetTrialBalanceAsync(client);
        Assert.Equal(100m, balances[cash]);
        Assert.Equal(-100m, balances[revenue]);
        Assert.Equal(PostingState.Posted, (await store.GetAsync(entry.Id))!.Posting);
    }

    [Fact]
    public async Task Voiding_reverses_the_entry_from_the_projection()
    {
        (LedgerService service, MongoJournalStore store, MongoBalanceProjection projection) = NewLedger();
        Guid client = Guid.NewGuid();
        Guid cash = Guid.NewGuid();
        Guid revenue = Guid.NewGuid();

        JournalEntry entry = Entry(client, 1, cash, revenue, 100m);
        await service.PostAsync(entry);
        await service.ApproveAsync(entry.Id, Guid.NewGuid());

        await service.VoidAsync(entry.Id);

        IReadOnlyDictionary<Guid, decimal> balances = await projection.GetTrialBalanceAsync(client);
        Assert.Equal(0m, balances[cash]);     // reversed back out
        Assert.Equal(0m, balances[revenue]);
        Assert.Equal(LifecycleStatus.Voided, (await store.GetAsync(entry.Id))!.Status);
    }

    [Fact]
    public async Task Revising_supersedes_the_original_and_nets_to_the_replacement()
    {
        (LedgerService service, MongoJournalStore store, MongoBalanceProjection projection) = NewLedger();
        Guid client = Guid.NewGuid();
        Guid cash = Guid.NewGuid();
        Guid revenue = Guid.NewGuid();

        JournalEntry original = Entry(client, 1, cash, revenue, 100m);
        await service.PostAsync(original);
        await service.ApproveAsync(original.Id, Guid.NewGuid());

        // Corrected amount, immediately on the books, linked to the original.
        JournalEntry replacement = Entry(client, 2, cash, revenue, 80m, PostingState.Posted, original.Id);
        await service.ReviseAsync(original.Id, replacement);

        IReadOnlyDictionary<Guid, decimal> balances = await projection.GetTrialBalanceAsync(client);
        Assert.Equal(80m, balances[cash]);      // 100 reversed, 80 applied
        Assert.Equal(-80m, balances[revenue]);

        JournalEntry? storedOriginal = await store.GetAsync(original.Id);
        Assert.Equal(LifecycleStatus.Superseded, storedOriginal!.Status);
        Assert.Equal(replacement.Id, storedOriginal.SupersededBy);

        JournalEntry? storedReplacement = await store.GetAsync(replacement.Id);
        Assert.Equal(LifecycleStatus.Active, storedReplacement!.Status);
        Assert.Equal(original.Id, storedReplacement.Supersedes);
    }
}
