using Accounting101.Ledger.Core.Journal;
using Accounting101.Ledger.Mongo.Documents;
using MongoDB.Driver;

namespace Accounting101.Ledger.Mongo.Tests;

// ── Head-maintenance tests ────────────────────────────────────────────────────
// These three tests verify the audit-head feature added in Task 1.
// They call AppendAsync directly so sequence counts are exact.

public sealed class AuditHeadTests(MongoFixture fixture) : IClassFixture<MongoFixture>
{
    private static Actor User() => new()
    {
        UserId = Guid.NewGuid(),
        Name = "tester",
        Claims = [new Claim("role", "tester")],
    };

    private (MongoAuditLog audit, IMongoCollection<AuditHeadDocument> head) NewAudit()
    {
        string auditCollection = "audit_head_" + Guid.NewGuid().ToString("N");
        MongoAuditLog audit = new(fixture.Database, auditCollection);
        IMongoCollection<AuditHeadDocument> head = fixture.Database.GetCollection<AuditHeadDocument>("audit-head");
        return (audit, head);
    }

    private Task AppendOne(MongoAuditLog audit, Guid clientId, IClientSessionHandle? session = null) =>
        audit.AppendAsync(
            clientId,
            entryId: Guid.NewGuid(),
            entryVersion: 1,
            action: AuditAction.Created,
            actor: User(),
            reason: null,
            at: DateTimeOffset.UtcNow,
            session: session);

    [Fact]
    public async Task Append_advances_the_head_to_the_latest_record()
    {
        (MongoAuditLog audit, IMongoCollection<AuditHeadDocument> head) = NewAudit();
        var clientId = Guid.NewGuid();

        await AppendOne(audit, clientId);
        await AppendOne(audit, clientId);
        await AppendOne(audit, clientId);

        AuditHeadDocument? doc = await head.Find(h => h.ClientId == clientId).FirstOrDefaultAsync();
        Assert.NotNull(doc);
        Assert.Equal(3, doc.Sequence);

        // Hash must match the 3rd record's hash
        IReadOnlyList<AuditRecordDocument> trail = await audit.GetForClientAsync(clientId);
        Assert.Equal(3, trail.Count);
        Assert.Equal(trail[2].Hash, doc.Hash);
    }

    [Fact]
    public async Task Head_does_not_regress_on_a_stale_update()
    {
        (MongoAuditLog audit, IMongoCollection<AuditHeadDocument> head) = NewAudit();
        var clientId = Guid.NewGuid();

        // Append 3 records normally — head should be at sequence 3
        await AppendOne(audit, clientId);
        await AppendOne(audit, clientId);
        await AppendOne(audit, clientId);

        AuditHeadDocument? before = await head.Find(h => h.ClientId == clientId).FirstOrDefaultAsync();
        Assert.NotNull(before);
        Assert.Equal(3, before.Sequence);

        // Directly invoke AdvanceHeadAsync with a stale (lower) sequence — must not regress
        await audit.AdvanceHeadAsync(clientId, sequence: 1, hash: "stale-hash");

        AuditHeadDocument? after = await head.Find(h => h.ClientId == clientId).FirstOrDefaultAsync();
        Assert.NotNull(after);
        Assert.Equal(3, after.Sequence);                     // still at 3
        Assert.Equal(before.Hash, after.Hash);               // hash unchanged
    }

    [Fact]
    public async Task Append_and_head_commit_together_in_a_transaction()
    {
        (MongoAuditLog audit, IMongoCollection<AuditHeadDocument> head) = NewAudit();
        var clientId = Guid.NewGuid();

        // ── Part A: commit ─────────────────────────────────────────────────
        using (IClientSessionHandle session = await fixture.Database.Client.StartSessionAsync())
        {
            session.StartTransaction();
            await AppendOne(audit, clientId, session);
            await session.CommitTransactionAsync();
        }

        IReadOnlyList<AuditRecordDocument> records = await audit.GetForClientAsync(clientId);
        Assert.Single(records);
        AuditHeadDocument? committed = await head.Find(h => h.ClientId == clientId).FirstOrDefaultAsync();
        Assert.NotNull(committed);
        Assert.Equal(1, committed.Sequence);

        // ── Part B: abort — neither the record NOR the head must advance ──
        long seqBefore = committed.Sequence;
        string hashBefore = committed.Hash;

        using (IClientSessionHandle session = await fixture.Database.Client.StartSessionAsync())
        {
            session.StartTransaction();
            await AppendOne(audit, clientId, session);
            await session.AbortTransactionAsync();
        }

        IReadOnlyList<AuditRecordDocument> recordsAfterAbort = await audit.GetForClientAsync(clientId);
        Assert.Single(recordsAfterAbort);                    // record rolled back

        AuditHeadDocument? headAfterAbort = await head.Find(h => h.ClientId == clientId).FirstOrDefaultAsync();
        Assert.NotNull(headAfterAbort);
        Assert.Equal(seqBefore, headAfterAbort.Sequence);    // head rolled back too
        Assert.Equal(hashBefore, headAfterAbort.Hash);
    }
}

// ── Existing AuditLogTests ────────────────────────────────────────────────────

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
        return (new LedgerService(fixture.Database.Client, store, projection, checkpoints, audit, new MongoSequenceStore(fixture.Database)), audit, auditCollection);
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
        var client = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

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
        var client = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

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
        var client = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

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

    [Fact]
    public async Task Tail_truncation_is_detected()
    {
        (LedgerService service, MongoAuditLog audit, string auditCollection) = NewLedger();
        var client = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        // Append 3 records
        JournalEntry e1 = Entry(client, 1, a, b, 10m);
        await service.PostAsync(e1, User());
        await service.ApproveAsync(e1.Id, User());
        JournalEntry e2 = Entry(client, 2, a, b, 20m);
        await service.PostAsync(e2, User());

        Assert.True(await audit.VerifyAsync(client));

        // Delete the newest record directly (tail truncation) - head doc is left intact
        IMongoCollection<AuditRecordDocument> raw = fixture.Database.GetCollection<AuditRecordDocument>(auditCollection);
        AuditRecordDocument newest = await raw.Find(r => r.ClientId == client).SortByDescending(r => r.Sequence).FirstAsync();
        await raw.DeleteOneAsync(r => r.Id == newest.Id);

        Assert.False(await audit.VerifyAsync(client));

        // Delete one more (two newest removed)
        AuditRecordDocument newestAgain = await raw.Find(r => r.ClientId == client).SortByDescending(r => r.Sequence).FirstAsync();
        await raw.DeleteOneAsync(r => r.Id == newestAgain.Id);

        Assert.False(await audit.VerifyAsync(client));
    }

    [Fact]
    public async Task Clean_chain_verifies_true()
    {
        (LedgerService service, MongoAuditLog audit, _) = NewLedger();
        var client = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        JournalEntry e1 = Entry(client, 1, a, b, 10m);
        await service.PostAsync(e1, User());
        await service.ApproveAsync(e1.Id, User());
        JournalEntry e2 = Entry(client, 2, a, b, 20m);
        await service.PostAsync(e2, User());

        Assert.True(await audit.VerifyAsync(client));
    }

    [Fact]
    public async Task Mid_deletion_is_detected()
    {
        (LedgerService service, MongoAuditLog audit, string auditCollection) = NewLedger();
        var client = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        JournalEntry e1 = Entry(client, 1, a, b, 10m);
        await service.PostAsync(e1, User());
        await service.ApproveAsync(e1.Id, User());
        JournalEntry e2 = Entry(client, 2, a, b, 20m);
        await service.PostAsync(e2, User());

        Assert.True(await audit.VerifyAsync(client));

        // Delete a middle record
        IMongoCollection<AuditRecordDocument> raw = fixture.Database.GetCollection<AuditRecordDocument>(auditCollection);
        List<AuditRecordDocument> all = await raw.Find(r => r.ClientId == client).SortBy(r => r.Sequence).ToListAsync();
        // Delete the second record (index 1), keeping first and last
        await raw.DeleteOneAsync(r => r.Id == all[1].Id);

        Assert.False(await audit.VerifyAsync(client));
    }

    [Fact]
    public async Task First_record_deletion_is_detected()
    {
        (LedgerService service, MongoAuditLog audit, string auditCollection) = NewLedger();
        var client = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        JournalEntry e1 = Entry(client, 1, a, b, 10m);
        await service.PostAsync(e1, User());
        await service.ApproveAsync(e1.Id, User());
        JournalEntry e2 = Entry(client, 2, a, b, 20m);
        await service.PostAsync(e2, User());

        IMongoCollection<AuditRecordDocument> raw = fixture.Database.GetCollection<AuditRecordDocument>(auditCollection);
        AuditRecordDocument first = await raw.Find(r => r.ClientId == client).SortBy(r => r.Sequence).FirstAsync();
        await raw.DeleteOneAsync(r => r.Id == first.Id);

        Assert.False(await audit.VerifyAsync(client));
    }

    [Fact]
    public async Task Sequence_gap_is_detected()
    {
        (LedgerService service, MongoAuditLog audit, string auditCollection) = NewLedger();
        var client = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        JournalEntry e1 = Entry(client, 1, a, b, 10m);
        await service.PostAsync(e1, User());
        await service.ApproveAsync(e1.Id, User());
        JournalEntry e2 = Entry(client, 2, a, b, 20m);
        await service.PostAsync(e2, User());

        Assert.True(await audit.VerifyAsync(client));

        // Perturb a record's Sequence to create a gap (e.g., change record with Sequence=2 to Sequence=99)
        IMongoCollection<AuditRecordDocument> raw = fixture.Database.GetCollection<AuditRecordDocument>(auditCollection);
        AuditRecordDocument last = await raw.Find(r => r.ClientId == client).SortByDescending(r => r.Sequence).FirstAsync();
        await raw.UpdateOneAsync(
            r => r.Id == last.Id,
            Builders<AuditRecordDocument>.Update.Set(r => r.Sequence, 99L));

        Assert.False(await audit.VerifyAsync(client));
    }

    [Fact]
    public async Task Empty_chain_with_no_head_verifies_true()
    {
        (_, MongoAuditLog audit, _) = NewLedger();
        var client = Guid.NewGuid();

        // No records, no head -> true
        Assert.True(await audit.VerifyAsync(client));
    }
}
