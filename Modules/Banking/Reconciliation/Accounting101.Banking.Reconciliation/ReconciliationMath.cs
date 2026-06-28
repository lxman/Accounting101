using Accounting101.Ledger.Contracts;

namespace Accounting101.Banking.Reconciliation;

/// <summary>The cleared-method reconciliation math — pure functions over ledger entries and a statement.</summary>
public static class ReconciliationMath
{
    /// <summary>The signed book cash effect of one entry on the cash account: Debit +Amount, Credit −Amount,
    /// summed over the entry's lines that touch the cash account.</summary>
    public static decimal CashEffect(EntryResponse entry, Guid cashAccountId) =>
        entry.Lines.Where(l => l.AccountId == cashAccountId)
            .Sum(l => string.Equals(l.Direction, "Debit", StringComparison.OrdinalIgnoreCase) ? l.Amount : -l.Amount);

    /// <summary>Σ cash effect of the entries whose id is in <paramref name="clearedIds"/>.</summary>
    public static decimal ClearedTotal(IEnumerable<EntryResponse> entries, IReadOnlySet<Guid> clearedIds, Guid cashAccountId) =>
        entries.Where(e => clearedIds.Contains(e.Id)).Sum(e => CashEffect(e, cashAccountId));

    public static decimal ReconciledDifference(decimal openingBalance, decimal closingBalance, decimal clearedTotal) =>
        closingBalance - (openingBalance + clearedTotal);

    public static bool IsBalanced(decimal reconciledDifference) => reconciledDifference == 0m;
}
