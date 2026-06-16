using Accounting101.Ledger.Core.Journal;
using MongoDB.Driver;

namespace Accounting101.Ledger.Mongo.Tests;

public sealed class AuditLogTests(MongoFixture fixture) : IClassFixture<MongoFixture>
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
        Claims = [new Claim("role", "approver")],
    };

    private (LedgerService service, MongoAuditLog audit, string auditCollection) NewLedger()
    {
        MongoJournalStore store = fixture.NewStore();
        MongoBalanceProjection projection = new(fixture.Database, store, "balances_" + Guid.NewGuid().ToString("N"));
        MongoCheckpointStore checkpoints = new(fixture.Database, "checkpoints_" + Guid.NewGuid().ToString("N"));
        string auditCollection = "audit_" + Guid.NewGuid().ToString("N");
        MongoAuditLog audit = new(fixture.Database, auditCollection);
        return (new LedgerService(store, projection, checkpoints, audit), audit, auditCollection);
    }

    private static JournalEntry Entry(Guid clientId, long sequence, Guid debit, Guid credit, decimal amount) =>
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
            ]);

    [Fact]
    public async Task Lifecycle_actions_are_recorded_with_the_principal_snapshot()
    {
        (LedgerService service, MongoAuditLog audit, _) = NewLedger();
        Guid client = Guid.NewGuid();
        Guid a = Guid.NewGuid();
        Guid b = Guid.NewGuid();

        Actor poster = User();
        Actor approver = User();

        JournalEntry entry = Entry(client, 1, a, b, 50m);
        await service.PostAsync(entry, poster);
        await service.ApproveAsync(entry.Id, approver);
        await service.VoidAsync(entry.Id, approver, "duplicate");

        IReadOnlyList<AuditRecordDocument> trail = await audit.GetForEntryAsync(client, entry.Id);

        Assert.Equal(3, trail.Count);
        Assert.Equal(AuditAction.Created, trail[0].Action);
        Assert.Equal(AuditAction.Approved, trail[1].Action);
        Assert.Equal(AuditAction.Voided, trail[2].Action);

        Assert.Equal(poster.UserId, trail[0].Actor.UserId);     // point-in-time principal snapshot
        Assert.Equal(approver.UserId, trail[1].Actor.UserId);
        Assert.Contains(trail[0].Actor.Claims, c => c.Type == "role" && c.Value == "approver"); // claims captured
        Assert.Equal("duplicate", trail[2].Reason);
    }

    [Fact]
    public async Task The_chain_verifies_after_a_sequence_of_actions()
    {
        (LedgerService service, MongoAuditLog audit, _) = NewLedger();
        Guid client = Guid.NewGuid();
        Guid a = Guid.NewGuid();
        Guid b = Guid.NewGuid();

        JournalEntry e1 = Entry(client, 1, a, b, 10m);
        await service.PostAsync(e1, User());
        await service.ApproveAsync(e1.Id, User());

        JournalEntry e2 = Entry(client, 2, a, b, 20m);
        await service.PostAsync(e2, User());
        await service.ApproveAsync(e2.Id, User());
        await service.CloseAsync(client, new DateOnly(2026, 6, 30), User());

        Assert.True(await audit.VerifyAsync(client));
    }

    [Fact]
    public async Task Tampering_with_a_record_breaks_the_chain()
    {
        (LedgerService service, MongoAuditLog audit, string auditCollection) = NewLedger();
        Guid client = Guid.NewGuid();
        Guid a = Guid.NewGuid();
        Guid b = Guid.NewGuid();

        JournalEntry entry = Entry(client, 1, a, b, 10m);
        await service.PostAsync(entry, User());
        await service.ApproveAsync(entry.Id, User());

        Assert.True(await audit.VerifyAsync(client));

        // Tamper directly in the collection, behind the log's back.
        IMongoCollection<AuditRecordDocument> raw = fixture.Database.GetCollection<AuditRecordDocument>(auditCollection);
        AuditRecordDocument first = await raw.Find(r => r.ClientId == client).SortBy(r => r.Sequence).FirstAsync();
        await raw.UpdateOneAsync(
            r => r.Id == first.Id,
            Builders<AuditRecordDocument>.Update.Set(r => r.Reason, "tampered"));

        Assert.False(await audit.VerifyAsync(client));
    }
}
