using Accounting101.Ledger.Core.Accounts;
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
        return (new LedgerService(fixture.Database.Client, store, projection, checkpoints, audit, new MongoSequenceStore(fixture.Database)), store, projection, checkpoints, audit);
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

    // ── Pending-gate tests (Task 3) ──────────────────────────────────────────

    [Fact]
    public async Task Close_is_blocked_by_an_in_period_pending_entry_and_does_not_freeze()
    {
        (LedgerService service, _, _, _, _) = NewLedger();
        var client = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        // Post (but do NOT approve) an entry dated 2024-06-30.
        await service.PostAsync(Entry(client, 1, new DateOnly(2024, 6, 30), a, b, 50m), User());

        PeriodCloseBlockedException ex = await Assert.ThrowsAsync<PeriodCloseBlockedException>(
            () => service.CloseAsync(client, new DateOnly(2024, 6, 30), User(), CancellationToken.None));
        Assert.Contains(ex.Blockers, bl => bl.EffectiveDate == new DateOnly(2024, 6, 30));

        // Not frozen: a fresh in-period post still succeeds (period stayed open).
        await service.PostAsync(Entry(client, 2, new DateOnly(2024, 6, 20), a, b, 10m), User());
    }

    [Fact]
    public async Task Close_succeeds_when_only_pending_entry_is_in_a_future_period()
    {
        (LedgerService service, _, _, _, _) = NewLedger();
        var client = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        // Pending entry dated 2024-07-10; closing through 2024-06-30 must succeed.
        await service.PostAsync(Entry(client, 1, new DateOnly(2024, 7, 10), a, b, 25m), User());

        await service.CloseAsync(client, new DateOnly(2024, 6, 30), User(), CancellationToken.None);
    }

    [Fact]
    public async Task Close_succeeds_when_all_in_period_entries_are_posted()
    {
        (LedgerService service, _, _, _, _) = NewLedger();
        var client = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        // Post and approve an entry — it is now Posted, not PendingApproval.
        await PostApproveAsync(service, Entry(client, 1, new DateOnly(2024, 6, 15), a, b, 100m));

        await service.CloseAsync(client, new DateOnly(2024, 6, 30), User(), CancellationToken.None);
    }

    [Fact]
    public async Task Voided_or_superseded_in_period_entry_does_not_block_close()
    {
        (LedgerService service, _, _, _, _) = NewLedger();
        var client = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        // Post and approve so we can void it (only approved entries can be voided).
        JournalEntry posted = await PostApproveAsync(service, Entry(client, 1, new DateOnly(2024, 6, 15), a, b, 200m));
        await service.VoidAsync(posted.Id, User());

        // Voided entry must not block the close.
        await service.CloseAsync(client, new DateOnly(2024, 6, 30), User(), CancellationToken.None);
    }

    [Fact]
    public async Task Blocked_close_is_resolved_by_approving_the_blocker_then_reclosing()
    {
        (LedgerService service, _, _, _, _) = NewLedger();
        var client = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        JournalEntry pending = Entry(client, 1, new DateOnly(2024, 6, 30), a, b, 75m);
        await service.PostAsync(pending, User());

        // First close attempt must throw.
        await Assert.ThrowsAsync<PeriodCloseBlockedException>(
            () => service.CloseAsync(client, new DateOnly(2024, 6, 30), User(), CancellationToken.None));

        // Approve the blocker — period is still open, so this is legal.
        await service.ApproveAsync(pending.Id, User());

        // Now close succeeds; the approved entry is on the books.
        IReadOnlyDictionary<Guid, decimal> snapshot =
            await service.CloseAsync(client, new DateOnly(2024, 6, 30), User(), CancellationToken.None);
        Assert.True(snapshot.ContainsKey(a) || snapshot.ContainsKey(b));
    }

    [Fact]
    public async Task Blocked_close_is_resolved_by_voiding_the_blocker_then_reclosing()
    {
        (LedgerService service, _, _, _, _) = NewLedger();
        var client = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        JournalEntry pending = Entry(client, 1, new DateOnly(2024, 6, 30), a, b, 75m);
        await service.PostAsync(pending, User());

        // First close attempt must throw.
        await Assert.ThrowsAsync<PeriodCloseBlockedException>(
            () => service.CloseAsync(client, new DateOnly(2024, 6, 30), User(), CancellationToken.None));

        // Approve then void (VoidAsync requires the entry to be Posted first).
        await service.ApproveAsync(pending.Id, User());
        await service.VoidAsync(pending.Id, User());

        // Close must now succeed; the voided entry is excluded from the snapshot.
        await service.CloseAsync(client, new DateOnly(2024, 6, 30), User(), CancellationToken.None);
    }

    [Fact]
    public async Task Year_end_close_is_blocked_by_an_unrelated_in_period_pending_entry()
    {
        (LedgerService service, _, _, _, _) = NewLedger();
        var client = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var retained = Guid.NewGuid();

        // Post but don't approve an entry dated inside the fiscal year.
        await service.PostAsync(Entry(client, 1, new DateOnly(2024, 6, 30), a, b, 50m), User());

        ChartOfAccounts chart = new(
        [
            new Account
            {
                Id = a,
                ClientId = client,
                Number = "4000",
                Name = "Revenue",
                Type = AccountType.Revenue,
                IsRetainedEarnings = false,
            },
            new Account
            {
                Id = b,
                ClientId = client,
                Number = "5000",
                Name = "Expense",
                Type = AccountType.Expense,
                IsRetainedEarnings = false,
            },
            new Account
            {
                Id = retained,
                ClientId = client,
                Number = "3900",
                Name = "Retained Earnings",
                Type = AccountType.Equity,
                IsRetainedEarnings = true,
            },
        ]);

        await Assert.ThrowsAsync<PeriodCloseBlockedException>(
            () => service.CloseYearAsync(client, new DateOnly(2024, 12, 31), User(), chart, CancellationToken.None));
    }
}
