namespace Accounting101.Ledger.Core.Journal;

/// <summary>
/// Pure derive-by-replay: folds journal entries into account balances. Only entries
/// that are *on the books* — <see cref="LifecycleStatus.Active"/> and
/// <see cref="PostingState.Posted"/> — contribute. Balances are debit-positive signed
/// sums: the cross-entry equivalent of <see cref="JournalEntry.BalanceFor"/>.
/// </summary>
public static class LedgerReplay
{
    /// <summary>An entry counts toward balances only when it is active and posted.</summary>
    public static bool IsOnBooks(JournalEntry entry) =>
        entry.Status == LifecycleStatus.Active && entry.Posting == PostingState.Posted;

    /// <summary>Net debit-positive balance for one account across the given entries.</summary>
    public static decimal BalanceFor(IEnumerable<JournalEntry> entries, Guid accountId) =>
        entries.Where(IsOnBooks).Sum(entry => entry.BalanceFor(accountId));

    /// <summary>
    /// Per-account balances — the trial balance. Optionally seeded with opening balances
    /// from a period checkpoint, so callers can replay only *since* the last close rather
    /// than from inception.
    /// </summary>
    public static IReadOnlyDictionary<Guid, decimal> Balances(
        IEnumerable<JournalEntry> entries,
        IReadOnlyDictionary<Guid, decimal>? opening = null)
    {
        Dictionary<Guid, decimal> balances = opening is null
            ? new Dictionary<Guid, decimal>()
            : new Dictionary<Guid, decimal>(opening);

        foreach (JournalEntry entry in entries.Where(IsOnBooks))
        {
            foreach (Line line in entry.Lines)
            {
                balances[line.AccountId] = balances.GetValueOrDefault(line.AccountId) + line.SignedEffect;
            }
        }

        return balances;
    }
}
