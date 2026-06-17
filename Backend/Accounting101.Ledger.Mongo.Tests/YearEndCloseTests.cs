using Accounting101.Ledger.Core.Accounts;
using Accounting101.Ledger.Core.Journal;

namespace Accounting101.Ledger.Mongo.Tests;

public sealed class YearEndCloseTests(MongoFixture fixture) : IClassFixture<MongoFixture>
{
    private static Actor User() => new()
    {
        UserId = Guid.NewGuid(),
        Name = "controller",
        Claims = [new Claim("role", "controller")],
    };

    private static AuditStamp Stamp() => new()
    {
        CreatedBy = Guid.NewGuid(),
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    private (LedgerService service, MongoBalanceProjection projection) NewLedger()
    {
        MongoJournalStore store = fixture.NewStore();
        MongoBalanceProjection projection = new(fixture.Database, store, "balances_" + Guid.NewGuid().ToString("N"));
        MongoCheckpointStore checkpoints = new(fixture.Database, "checkpoints_" + Guid.NewGuid().ToString("N"));
        MongoAuditLog audit = new(fixture.Database, "audit_" + Guid.NewGuid().ToString("N"));
        return (new LedgerService(store, projection, checkpoints, audit), projection);
    }

    private static Account Acct(Guid client, string number, string name, AccountType type, bool retainedEarnings = false) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = client,
        Number = number,
        Name = name,
        Type = type,
        IsRetainedEarnings = retainedEarnings,
    };

    private static JournalEntry Entry(Guid client, long sequence, DateOnly date, Guid debit, Guid credit, decimal amount) =>
        JournalEntry.Create(
            id: Guid.NewGuid(),
            clientId: client,
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

    [Fact]
    public async Task Year_end_close_resets_temporaries_into_retained_earnings()
    {
        (LedgerService service, MongoBalanceProjection projection) = NewLedger();
        var client = Guid.NewGuid();

        Account cash = Acct(client, "1000", "Cash", AccountType.Asset);
        Account revenue = Acct(client, "4000", "Revenue", AccountType.Revenue);
        Account expense = Acct(client, "5000", "Expense", AccountType.Expense);
        Account retained = Acct(client, "3900", "Retained Earnings", AccountType.Equity, retainedEarnings: true);
        ChartOfAccounts chart = new([cash, revenue, expense, retained]);

        // Revenue 1000, expense 600 → net income 400.
        JournalEntry sale = Entry(client, 1, new DateOnly(2026, 6, 30), cash.Id, revenue.Id, 1000m);
        await service.PostAsync(sale, User());
        await service.ApproveAsync(sale.Id, User());

        JournalEntry cost = Entry(client, 2, new DateOnly(2026, 9, 30), expense.Id, cash.Id, 600m);
        await service.PostAsync(cost, User());
        await service.ApproveAsync(cost.Id, User());

        JournalEntry? closing = await service.CloseYearAsync(
            client, new DateOnly(2026, 12, 31), User(), chart, closingSequenceNumber: 1000);

        IReadOnlyDictionary<Guid, decimal> balances = await projection.GetTrialBalanceAsync(client);
        Assert.Equal(0m, balances.GetValueOrDefault(revenue.Id));   // temporary reset
        Assert.Equal(0m, balances.GetValueOrDefault(expense.Id));   // temporary reset
        Assert.Equal(-400m, balances[retained.Id]);                 // net income rolled into RE (credit 400)
        Assert.Equal(400m, balances[cash.Id]);                      // permanent — carried forward

        Assert.NotNull(closing);
        Assert.Equal(EntryType.Closing, closing!.Type);

        // The year is now frozen.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PostAsync(Entry(client, 3, new DateOnly(2026, 11, 15), cash.Id, revenue.Id, 50m), User()));
    }

    [Fact]
    public async Task Year_end_close_with_no_activity_just_closes()
    {
        (LedgerService service, _) = NewLedger();
        var client = Guid.NewGuid();
        ChartOfAccounts chart = new([Acct(client, "3900", "Retained Earnings", AccountType.Equity, retainedEarnings: true)]);

        JournalEntry? closing = await service.CloseYearAsync(client, new DateOnly(2026, 12, 31), User(), chart, closingSequenceNumber: 1);

        Assert.Null(closing); // nothing to close
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PostAsync(Entry(client, 2, new DateOnly(2026, 12, 1), Guid.NewGuid(), Guid.NewGuid(), 10m), User()));
    }
}
