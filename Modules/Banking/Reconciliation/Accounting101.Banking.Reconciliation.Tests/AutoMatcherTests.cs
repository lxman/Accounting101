using Accounting101.Banking.Reconciliation;

namespace Accounting101.Banking.Reconciliation.Tests;

public sealed class AutoMatcherTests
{
    private static BankStatementLine Line(decimal amount, int day = 15) =>
        new(new DateOnly(2026, 1, day), amount, $"line {amount}", null);

    private static MatchableEntry Entry(Guid id, decimal cashEffect, int day = 15) =>
        new(id, new DateOnly(2026, 1, day), cashEffect);

    [Fact]
    public void Pairs_lines_to_entries_by_exact_signed_amount()
    {
        Guid dep = Guid.NewGuid(), pay = Guid.NewGuid();
        // +100 deposit line ↔ +100 entry; −40 payment line ↔ −40 entry.
        AutoMatchProposal p = AutoMatcher.Match(
            [Line(100m), Line(-40m)],
            [Entry(dep, 100m), Entry(pay, -40m)]);

        Assert.Equal(2, p.Matches.Count);
        Assert.Empty(p.UnmatchedStatementLines);
        Assert.Empty(p.UnmatchedEntries);
        Assert.Contains(p.Matches, m => m.StatementLineIndex == 0 && m.EntryId == dep && m.Amount == 100m);
        Assert.Contains(p.Matches, m => m.StatementLineIndex == 1 && m.EntryId == pay && m.Amount == -40m);
        Assert.Equal(new[] { dep, pay }.Order(), p.MatchedEntryIds.Order()); // MatchedEntryIds mirrors Matches
    }

    [Fact]
    public void When_amounts_tie_it_picks_the_nearest_date_entry()
    {
        Guid near = Guid.NewGuid(), far = Guid.NewGuid();
        // One +100 line on day 15; two +100 entries (day 14 near, day 1 far) → near wins, far is unmatched.
        AutoMatchProposal p = AutoMatcher.Match(
            [Line(100m, day: 15)],
            [Entry(far, 100m, day: 1), Entry(near, 100m, day: 14)]);

        Assert.Single(p.Matches);
        Assert.Equal(near, p.Matches[0].EntryId);
        Assert.Equal(1, p.Matches[0].DaysApart); // |14 − 15|
        Assert.Single(p.UnmatchedEntries);
        Assert.Equal(far, p.UnmatchedEntries[0].EntryId);
    }

    [Fact]
    public void A_line_with_no_matching_amount_is_reported_unmatched()
    {
        Guid e = Guid.NewGuid();
        AutoMatchProposal p = AutoMatcher.Match(
            [Line(100m), Line(77m)],          // 77 has no entry
            [Entry(e, 100m)]);

        Assert.Single(p.Matches);
        Assert.Single(p.UnmatchedStatementLines);
        Assert.Equal(1, p.UnmatchedStatementLines[0].StatementLineIndex);
        Assert.Equal(77m, p.UnmatchedStatementLines[0].Amount);
        Assert.Empty(p.UnmatchedEntries);
    }

    [Fact]
    public void Each_entry_is_consumed_at_most_once()
    {
        Guid only = Guid.NewGuid();
        // Two +50 lines but only one +50 entry → one match, one unmatched line, no leftover entry.
        AutoMatchProposal p = AutoMatcher.Match(
            [Line(50m), Line(50m)],
            [Entry(only, 50m)]);

        Assert.Single(p.Matches);
        Assert.Single(p.UnmatchedStatementLines);
        Assert.Empty(p.UnmatchedEntries);
    }
}
