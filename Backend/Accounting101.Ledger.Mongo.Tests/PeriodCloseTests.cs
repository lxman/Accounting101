using Accounting101.Ledger.Core.Journal;

namespace Accounting101.Ledger.Mongo.Tests;

public sealed class PeriodCloseTests(MongoFixture fixture) : IClassFixture<MongoFixture>
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
        Claims = [new Claim("role", "controller")],
    };

    private (LedgerService service, MongoJournalStore store, MongoBalanceProjection projection, MongoCheckpointStore checkpoints, MongoAuditLog audit) NewLedger()
    {
        MongoJournalStore store = fixture.NewStore();
        MongoBalanceProjection projection = new(fixture.Database, store, "balances_" + Guid.NewGuid().ToString("N"));
        MongoCheckpointStore checkpoints = new(fixture.Database, "checkpoints_" + Guid.NewGuid().ToString("N"));
        MongoAuditLog audit = new(fixture.Database, "audit_" + Guid.NewGuid().ToString("N"));
        return (new LedgerService(fixture.Database.Client, store, projection, checkpoints, audit), store, projection, checkpoints, audit);
    }

    private static JournalEntry Entry(Guid clientId, long sequence, DateOnly date, Guid debit, Guid credit, decimal amount) =>
        JournalEntry.Create(
            id: Guid.NewGuid(),
            clientId: clientId,
            sequenceNumber: sequence,
            effectiveDate: date,
            postedAt: DateTimeOffset.UnixEpoch,
            type: EntryType.Standard,
            audit: Stamp(),
            lines:
            [
                new Line { Id = Guid.NewGuid(), AccountId = debit, Direction = Direction.Debit, Amount = amount },
                new Line { Id = Guid.NewGuid(), AccountId = credit, Direction = Direction.Credit, Amount = amount },
            ]);

    private static async Task<JournalEntry> PostApproveAsync(LedgerService service, JournalEntry entry)
    {
        await service.PostAsync(entry, User());
        await service.ApproveAsync(entry.Id, User());
        return entry;
    }

    [Fact]
    public async Task Closing_snapshots_the_period_end_balances()
    {
        (LedgerService service, _, _, MongoCheckpointStore checkpoints, _) = NewLedger();
        var client = Guid.NewGuid();
        var cash = Guid.NewGuid();
        var revenue = Guid.NewGuid();

        await PostApproveAsync(service, Entry(client, 1, new DateOnly(2026, 3, 10), cash, revenue, 100m));
        await PostApproveAsync(service, Entry(client, 2, new DateOnly(2026, 3, 20), cash, revenue, 50m));
        await PostApproveAsync(service, Entry(client, 3, new DateOnly(2026, 4, 5), cash, revenue, 999m)); // April — excluded

        IReadOnlyDictionary<Guid, decimal> snapshot = await service.CloseAsync(client, new DateOnly(2026, 3, 31), User());

        Assert.Equal(150m, snapshot[cash]);     // 100 + 50, March only
        Assert.Equal(-150m, snapshot[revenue]);

        Assert.Equal(new DateOnly(2026, 3, 31), await checkpoints.GetClosedThroughAsync(client));
        IReadOnlyDictionary<Guid, decimal> opening = await checkpoints.GetOpeningBalancesAsync(client);
        Assert.Equal(150m, opening[cash]);       // the checkpoint is next period's opening balance
    }

    [Fact]
    public async Task A_closed_period_rejects_backdated_posts_but_allows_open_ones()
    {
        (LedgerService service, _, _, _, _) = NewLedger();
        var client = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        await service.CloseAsync(client, new DateOnly(2026, 3, 31), User());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PostAsync(Entry(client, 1, new DateOnly(2026, 3, 15), a, b, 10m), User())); // back-dated into closed period

        await service.PostAsync(Entry(client, 2, new DateOnly(2026, 4, 1), a, b, 10m), User()); // open period — fine
    }

    [Fact]
    public async Task A_closed_period_entry_cannot_be_voided()
    {
        (LedgerService service, _, _, _, _) = NewLedger();
        var client = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        JournalEntry march = await PostApproveAsync(service, Entry(client, 1, new DateOnly(2026, 3, 15), a, b, 10m));
        await service.CloseAsync(client, new DateOnly(2026, 3, 31), User());

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.VoidAsync(march.Id, User()));
    }

    [Fact]
    public async Task Cannot_close_at_or_before_the_last_close()
    {
        (LedgerService service, _, _, _, _) = NewLedger();
        var client = Guid.NewGuid();

        await service.CloseAsync(client, new DateOnly(2026, 3, 31), User());

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CloseAsync(client, new DateOnly(2026, 3, 31), User()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CloseAsync(client, new DateOnly(2026, 2, 28), User()));

        await service.CloseAsync(client, new DateOnly(2026, 4, 30), User()); // a later period — fine
    }
}
