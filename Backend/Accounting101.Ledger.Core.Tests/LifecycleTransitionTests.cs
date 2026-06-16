using Accounting101.Ledger.Core.Journal;

namespace Accounting101.Ledger.Core.Tests;

public class LifecycleTransitionTests
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();

    private static JournalEntry Pending() => JournalEntry.Create(
        id: Guid.NewGuid(),
        clientId: Guid.NewGuid(),
        sequenceNumber: 1,
        effectiveDate: new DateOnly(2026, 1, 1),
        postedAt: DateTimeOffset.UnixEpoch,
        type: EntryType.Standard,
        audit: new AuditStamp { CreatedBy = Guid.NewGuid(), CreatedAt = DateTimeOffset.UnixEpoch },
        lines:
        [
            new Line { Id = Guid.NewGuid(), AccountId = A, Direction = Direction.Debit, Amount = 100m },
            new Line { Id = Guid.NewGuid(), AccountId = B, Direction = Direction.Credit, Amount = 100m },
        ]);

    [Fact]
    public void Approve_puts_a_pending_entry_on_the_books()
    {
        Guid approver = Guid.NewGuid();
        JournalEntry approved = Pending().Approve(approver);

        Assert.Equal(PostingState.Posted, approved.Posting);
        Assert.Equal(LifecycleStatus.Active, approved.Status);
        Assert.Equal(approver, approved.Audit.ApprovedBy);
        Assert.True(LedgerReplay.IsOnBooks(approved));
    }

    [Fact]
    public void Approve_rejects_an_already_posted_entry()
    {
        JournalEntry posted = Pending().Approve(Guid.NewGuid());
        Assert.Throws<InvalidOperationException>(() => posted.Approve(Guid.NewGuid()));
    }

    [Fact]
    public void Void_marks_an_active_entry_voided_and_off_the_books()
    {
        JournalEntry voided = Pending().Approve(Guid.NewGuid()).Void();

        Assert.Equal(LifecycleStatus.Voided, voided.Status);
        Assert.False(LedgerReplay.IsOnBooks(voided));
    }

    [Fact]
    public void Void_rejects_an_inactive_entry()
    {
        JournalEntry voided = Pending().Void();
        Assert.Throws<InvalidOperationException>(() => voided.Void());
    }

    [Fact]
    public void SupersededBy_links_and_marks_superseded()
    {
        Guid replacementId = Guid.NewGuid();
        JournalEntry superseded = Pending().Supersede(replacementId);

        Assert.Equal(LifecycleStatus.Superseded, superseded.Status);
        Assert.Equal(replacementId, superseded.SupersededBy);
        Assert.False(LedgerReplay.IsOnBooks(superseded));
    }
}
