using Accounting101.Ledger.Core.Journal;

namespace Accounting101.Ledger.Mongo.Tests;

public sealed class ReversalSourceOverrideTests(MongoFixture fixture) : IClassFixture<MongoFixture>
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
        return (new LedgerService(fixture.Database.Client, store, projection, checkpoints, audit, new MongoSequenceStore(fixture.Database)), store, projection, checkpoints, audit);
    }

    private static JournalEntry Entry(
        Guid clientId, Guid debit, Guid credit, decimal amount, Guid? sourceRef, string? sourceType) =>
        JournalEntry.Create(
            id: Guid.NewGuid(),
            clientId: clientId,
            sequenceNumber: 0,
            effectiveDate: new DateOnly(2026, 6, 1),
            postedAt: DateTimeOffset.UnixEpoch,
            type: EntryType.Standard,
            audit: Stamp(),
            lines:
            [
                new Line { Id = Guid.NewGuid(), AccountId = debit, Direction = Direction.Debit, Amount = amount },
                new Line { Id = Guid.NewGuid(), AccountId = credit, Direction = Direction.Credit, Amount = amount },
            ],
            sourceRef: sourceRef,
            sourceType: sourceType);

    [Fact]
    public async Task Reverse_with_source_override_tags_the_reversal_with_its_own_document()
    {
        (LedgerService service, _, _, _, _) = NewLedger();
        var client = Guid.NewGuid();
        var cash = Guid.NewGuid();
        var revenue = Guid.NewGuid();
        Actor actor = User();
        Guid origDoc = Guid.NewGuid();

        JournalEntry original = Entry(client, cash, revenue, 100m, origDoc, "invoice");
        await service.PostAsync(original, actor);
        await service.ApproveAsync(original.Id, actor);

        Guid myDoc = Guid.NewGuid();
        JournalEntry reversal = await service.ReverseAsync(
            original.Id, new DateOnly(2026, 6, 15), actor, reason: "credit memo",
            sourceRef: myDoc, sourceType: "credit-memo");

        Assert.Equal(myDoc, reversal.SourceRef);
        Assert.Equal("credit-memo", reversal.SourceType);
    }

    [Fact]
    public async Task Reverse_without_override_inherits_the_originals_source()
    {
        (LedgerService service, _, _, _, _) = NewLedger();
        var client = Guid.NewGuid();
        var cash = Guid.NewGuid();
        var revenue = Guid.NewGuid();
        Actor actor = User();
        Guid origDoc = Guid.NewGuid();

        JournalEntry original = Entry(client, cash, revenue, 100m, origDoc, "invoice");
        await service.PostAsync(original, actor);
        await service.ApproveAsync(original.Id, actor);

        JournalEntry reversal = await service.ReverseAsync(original.Id, new DateOnly(2026, 6, 15), actor);

        Assert.Equal(original.SourceRef, reversal.SourceRef);
        Assert.Equal(original.SourceType, reversal.SourceType);
    }
}
