using Accounting101.Ledger.Core.Journal;

namespace Accounting101.Ledger.Core.Tests;

public class JournalEntryTests
{
    private static readonly Guid AcctB = Guid.NewGuid();
    private static readonly Guid AcctD = Guid.NewGuid();
    private static readonly Guid AcctF = Guid.NewGuid();

    private static AuditStamp Stamp() => new()
    {
        CreatedBy = Guid.NewGuid(),
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    private static Line L(Direction direction, Guid accountId, decimal amount) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = accountId,
        Direction = direction,
        Amount = amount,
    };

    private static JournalEntry Entry(params Line[] lines) => JournalEntry.Create(
        id: Guid.NewGuid(),
        clientId: Guid.NewGuid(),
        sequenceNumber: 1,
        effectiveDate: new DateOnly(2026, 1, 1),
        postedAt: DateTimeOffset.UnixEpoch,
        type: EntryType.Standard,
        audit: Stamp(),
        lines: lines);

    [Fact]
    public void Balanced_split_entry_is_created()
    {
        // $100 out of B, split to D $60 and F $40  (60 + 40 == 100).
        JournalEntry entry = Entry(
            L(Direction.Debit, AcctD, 60m),
            L(Direction.Debit, AcctF, 40m),
            L(Direction.Credit, AcctB, 100m));

        Assert.Equal(3, entry.Lines.Count);
        Assert.Equal(0m, entry.SignedTotal());
    }

    [Fact]
    public void Simple_two_line_pair_is_created()
    {
        JournalEntry entry = Entry(
            L(Direction.Debit, AcctD, 100m),
            L(Direction.Credit, AcctB, 100m));

        Assert.Equal(0m, entry.SignedTotal());
    }

    [Fact]
    public void Single_line_entry_is_rejected()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            Entry(L(Direction.Debit, AcctD, 100m)));

        Assert.Equal("lines", ex.ParamName);
    }

    [Fact]
    public void Unbalanced_entry_is_rejected_and_reports_the_imbalance()
    {
        UnbalancedEntryException ex = Assert.Throws<UnbalancedEntryException>(() =>
            Entry(
                L(Direction.Debit, AcctD, 100m),
                L(Direction.Credit, AcctB, 90m)));

        Assert.Equal(10m, ex.Imbalance); // debits exceed credits by 10
    }

    [Fact]
    public void Negative_debit_nets_like_a_credit()
    {
        // A negative debit to B has the same effect as a positive credit to B.
        JournalEntry entry = Entry(
            L(Direction.Debit, AcctD, 100m),
            L(Direction.Debit, AcctB, -100m));

        Assert.Equal(0m, entry.SignedTotal());
        Assert.Equal(100m, entry.BalanceFor(AcctD));
        Assert.Equal(-100m, entry.BalanceFor(AcctB));
    }

    [Fact]
    public void BalanceFor_returns_per_account_net_effect()
    {
        JournalEntry entry = Entry(
            L(Direction.Debit, AcctD, 60m),
            L(Direction.Debit, AcctF, 40m),
            L(Direction.Credit, AcctB, 100m));

        Assert.Equal(60m, entry.BalanceFor(AcctD));
        Assert.Equal(40m, entry.BalanceFor(AcctF));
        Assert.Equal(-100m, entry.BalanceFor(AcctB));
        Assert.Equal(0m, entry.BalanceFor(Guid.NewGuid())); // untouched account
    }

    [Fact]
    public void New_entry_defaults_to_active_and_pending_approval()
    {
        JournalEntry entry = Entry(
            L(Direction.Debit, AcctD, 1m),
            L(Direction.Credit, AcctB, 1m));

        Assert.Equal(LifecycleStatus.Active, entry.Status);
        Assert.Equal(PostingState.PendingApproval, entry.Posting);
    }

    [Fact]
    public void Builder_assembles_a_balanced_entry()
    {
        JournalEntry entry = new JournalEntryBuilder(
                id: Guid.NewGuid(),
                clientId: Guid.NewGuid(),
                sequenceNumber: 1,
                effectiveDate: new DateOnly(2026, 1, 1),
                postedAt: DateTimeOffset.UnixEpoch,
                audit: Stamp())
            { Memo = "payroll split" }
            .Debit(AcctD, 60m)
            .Debit(AcctF, 40m)
            .Credit(AcctB, 100m)
            .Build();

        Assert.Equal(3, entry.Lines.Count);
        Assert.Equal(0m, entry.SignedTotal());
        Assert.Equal("payroll split", entry.Memo);
    }

    [Fact]
    public void Builder_rejects_an_unbalanced_entry()
    {
        JournalEntryBuilder builder = new JournalEntryBuilder(
                id: Guid.NewGuid(),
                clientId: Guid.NewGuid(),
                sequenceNumber: 1,
                effectiveDate: new DateOnly(2026, 1, 1),
                postedAt: DateTimeOffset.UnixEpoch,
                audit: Stamp())
            .Debit(AcctD, 100m)
            .Credit(AcctB, 99m);

        Assert.Throws<UnbalancedEntryException>(builder.Build);
    }
}
