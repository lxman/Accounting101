using Accounting101.Ledger.Core.Journal;

namespace Accounting101.Ledger.Core.Tests;

public class LedgerReplayTests
{
    private static readonly Guid Cash = Guid.NewGuid();
    private static readonly Guid Revenue = Guid.NewGuid();
    private static readonly Guid Rent = Guid.NewGuid();

    private static AuditStamp Stamp() => new()
    {
        CreatedBy = Guid.NewGuid(),
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    private static JournalEntry Entry(
        PostingState posting,
        LifecycleStatus status,
        params (Direction dir, Guid acct, decimal amt)[] lines)
    {
        IReadOnlyList<Line> mapped = lines
            .Select(l => new Line { Id = Guid.NewGuid(), AccountId = l.acct, Direction = l.dir, Amount = l.amt })
            .ToList();

        return JournalEntry.Create(
            id: Guid.NewGuid(),
            clientId: Guid.NewGuid(),
            sequenceNumber: 1,
            effectiveDate: new DateOnly(2026, 1, 1),
            postedAt: DateTimeOffset.UnixEpoch,
            type: EntryType.Standard,
            audit: Stamp(),
            lines: mapped,
            posting: posting,
            status: status);
    }

    private static JournalEntry Posted(params (Direction dir, Guid acct, decimal amt)[] lines) =>
        Entry(PostingState.Posted, LifecycleStatus.Active, lines);

    [Fact]
    public void Balances_fold_posted_active_entries_per_account()
    {
        JournalEntry[] entries =
        [
            Posted((Direction.Debit, Cash, 100m), (Direction.Credit, Revenue, 100m)),
            Posted((Direction.Debit, Rent, 30m), (Direction.Credit, Cash, 30m)),
        ];

        IReadOnlyDictionary<Guid, decimal> balances = LedgerReplay.Balances(entries);

        Assert.Equal(70m, balances[Cash]);       // 100 - 30
        Assert.Equal(-100m, balances[Revenue]);
        Assert.Equal(30m, balances[Rent]);
    }

    [Fact]
    public void Replay_gate_excludes_pending_and_superseded_entries()
    {
        JournalEntry[] entries =
        [
            Posted((Direction.Debit, Cash, 100m), (Direction.Credit, Revenue, 100m)),
            Entry(PostingState.PendingApproval, LifecycleStatus.Active,
                (Direction.Debit, Cash, 999m), (Direction.Credit, Revenue, 999m)),
            Entry(PostingState.Posted, LifecycleStatus.Superseded,
                (Direction.Debit, Cash, 555m), (Direction.Credit, Revenue, 555m)),
        ];

        // Only the posted + active entry is on the books.
        Assert.Equal(100m, LedgerReplay.BalanceFor(entries, Cash));
    }

    [Fact]
    public void Balances_compose_with_a_checkpoint_opening()
    {
        Dictionary<Guid, decimal> opening = new() { [Cash] = 500m };
        JournalEntry[] sinceCheckpoint =
        [
            Posted((Direction.Debit, Cash, 25m), (Direction.Credit, Revenue, 25m)),
        ];

        IReadOnlyDictionary<Guid, decimal> balances = LedgerReplay.Balances(sinceCheckpoint, opening);

        Assert.Equal(525m, balances[Cash]); // 500 opening + 25 since the checkpoint
    }

    [Fact]
    public void Trial_balance_sums_to_zero()
    {
        JournalEntry[] entries =
        [
            Posted((Direction.Debit, Cash, 100m), (Direction.Credit, Revenue, 100m)),
            Posted((Direction.Debit, Rent, 30m), (Direction.Credit, Cash, 30m)),
        ];

        decimal trialBalanceTotal = LedgerReplay.Balances(entries).Values.Sum();

        Assert.Equal(0m, trialBalanceTotal); // the double-entry invariant, in aggregate
    }
}
