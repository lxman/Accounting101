namespace Accounting101.Banking.Reconciliation;

/// <summary>An eligible, uncleared ledger entry prepared for matching: its id, date, and signed book cash
/// effect (Debit-to-cash +, Credit −) — the value that must equal a bank statement line's signed amount.</summary>
public sealed record MatchableEntry(Guid EntryId, DateOnly Date, decimal CashEffect);

/// <summary>A proposed pairing of one statement line (by its index in the statement) to one ledger entry.</summary>
public sealed record AutoMatch(
    int StatementLineIndex, decimal Amount, Guid EntryId, DateOnly LineDate, DateOnly EntryDate, int DaysApart);

/// <summary>A statement line that no uncleared eligible entry matched, by index.</summary>
public sealed record UnmatchedLine(int StatementLineIndex, DateOnly Date, decimal Amount, string Description);

/// <summary>The auto-match proposal: the pairings, the lines and entries left unmatched on each side, and a
/// flat list of the matched entry ids (ready to hand to /clear). Pure data — auto-match mutates nothing.</summary>
public sealed record AutoMatchProposal(
    IReadOnlyList<AutoMatch> Matches,
    IReadOnlyList<UnmatchedLine> UnmatchedStatementLines,
    IReadOnlyList<MatchableEntry> UnmatchedEntries,
    IReadOnlyList<Guid> MatchedEntryIds);

/// <summary>Pairs bank statement lines to uncleared eligible ledger entries by exact signed amount, 1:1,
/// breaking amount ties by nearest date (then entry id, for determinism). Pure — no ledger or store access.</summary>
public static class AutoMatcher
{
    public static AutoMatchProposal Match(
        IReadOnlyList<BankStatementLine> statementLines, IReadOnlyList<MatchableEntry> uncleared)
    {
        List<MatchableEntry> remaining = uncleared.ToList();
        List<AutoMatch> matches = [];
        List<UnmatchedLine> unmatchedLines = [];

        for (int i = 0; i < statementLines.Count; i++)
        {
            BankStatementLine line = statementLines[i];
            MatchableEntry? best = remaining
                .Where(e => e.CashEffect == line.Amount)
                .OrderBy(e => Math.Abs(e.Date.DayNumber - line.Date.DayNumber))
                .ThenBy(e => e.EntryId)
                .FirstOrDefault();

            if (best is null)
            {
                unmatchedLines.Add(new UnmatchedLine(i, line.Date, line.Amount, line.Description));
                continue;
            }

            remaining.Remove(best);
            int daysApart = Math.Abs(best.Date.DayNumber - line.Date.DayNumber);
            matches.Add(new AutoMatch(i, line.Amount, best.EntryId, line.Date, best.Date, daysApart));
        }

        return new AutoMatchProposal(matches, unmatchedLines, remaining, matches.Select(m => m.EntryId).ToList());
    }
}
