using Accounting101.Ledger.Core.Journal;

namespace Accounting101.Ledger.Mongo.Tests;

public sealed class LedgerServiceTests(MongoFixture fixture) : IClassFixture<MongoFixture>
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

    private (LedgerService service, MongoJournalStore store, MongoBalanceProjection projection, MongoCheckpointStore checkpoints, MongoAuditLog audit) NewLedger()
    {
        MongoJournalStore store = fixture.NewStore();
        MongoBalanceProjection projection = new(fixture.Database, store, "balances_" + Guid.NewGuid().ToString("N"));
        MongoCheckpointStore checkpoints = new(fixture.Database, "checkpoints_" + Guid.NewGuid().ToString("N"));
        MongoAuditLog audit = new(fixture.Database, "audit_" + Guid.NewGuid().ToString("N"));
        return (new LedgerService(fixture.Database.Client, store, projection, checkpoints, audit), store, projection, checkpoints, audit);
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
        (LedgerService service, MongoJournalStore store, MongoBalanceProjection projection, _, _) = NewLedger();
        var client = Guid.NewGuid();
        var cash = Guid.NewGuid();
        var revenue = Guid.NewGuid();

        JournalEntry entry = Entry(client, 1, cash, revenue, 100m);
        await service.PostAsync(entry, User());

        Assert.Empty(await projection.GetTrialBalanceAsync(client)); // pending: not on the books

        await service.ApproveAsync(entry.Id, User());

        IReadOnlyDictionary<Guid, decimal> balances = await projection.GetTrialBalanceAsync(client);
        Assert.Equal(100m, balances[cash]);
        Assert.Equal(-100m, balances[revenue]);
        Assert.Equal(PostingState.Posted, (await store.GetAsync(entry.Id))!.Posting);
    }

    [Fact]
    public async Task Voiding_reverses_the_entry_from_the_projection()
    {
        (LedgerService service, MongoJournalStore store, MongoBalanceProjection projection, _, _) = NewLedger();
        var client = Guid.NewGuid();
        var cash = Guid.NewGuid();
        var revenue = Guid.NewGuid();

        JournalEntry entry = Entry(client, 1, cash, revenue, 100m);
        await service.PostAsync(entry, User());
        await service.ApproveAsync(entry.Id, User());

        await service.VoidAsync(entry.Id, User());

        IReadOnlyDictionary<Guid, decimal> balances = await projection.GetTrialBalanceAsync(client);
        Assert.Equal(0m, balances[cash]);     // reversed back out
        Assert.Equal(0m, balances[revenue]);
        Assert.Equal(LifecycleStatus.Voided, (await store.GetAsync(entry.Id))!.Status);
    }

    [Fact]
    public async Task A_revision_has_no_effect_until_approved_then_it_swaps()
    {
        (LedgerService service, MongoJournalStore store, MongoBalanceProjection projection, _, _) = NewLedger();
        var client = Guid.NewGuid();
        var cash = Guid.NewGuid();
        var revenue = Guid.NewGuid();

        JournalEntry original = Entry(client, 1, cash, revenue, 100m);
        await service.PostAsync(original, User());
        await service.ApproveAsync(original.Id, User());

        // Propose a correction (80). A revision is pending: no effect on the books or the original yet.
        JournalEntry replacement = Entry(client, 2, cash, revenue, 80m, supersedes: original.Id);
        await service.ReviseAsync(original.Id, replacement, User());

        IReadOnlyDictionary<Guid, decimal> afterRevise = await projection.GetTrialBalanceAsync(client);
        Assert.Equal(100m, afterRevise[cash]); // unchanged — the revision does not exist until approved
        Assert.Equal(LifecycleStatus.Active, (await store.GetAsync(original.Id))!.Status);
        JournalEntry? pending = await store.GetAsync(replacement.Id);
        Assert.Equal(PostingState.PendingApproval, pending!.Posting);
        Assert.Equal(original.Id, pending.Supersedes);

        // Approving the replacement swaps atomically: it posts, the original is superseded.
        await service.ApproveAsync(replacement.Id, User());

        IReadOnlyDictionary<Guid, decimal> afterApprove = await projection.GetTrialBalanceAsync(client);
        Assert.Equal(80m, afterApprove[cash]);   // 100 reversed, 80 applied — at approval, no gap
        Assert.Equal(-80m, afterApprove[revenue]);

        JournalEntry? storedOriginal = await store.GetAsync(original.Id);
        Assert.Equal(LifecycleStatus.Superseded, storedOriginal!.Status);
        Assert.Equal(replacement.Id, storedOriginal.SupersededBy);

        JournalEntry? storedReplacement = await store.GetAsync(replacement.Id);
        Assert.Equal(PostingState.Posted, storedReplacement!.Posting);
        Assert.Equal(original.Id, storedReplacement.Supersedes);
    }
}
