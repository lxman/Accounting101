using Accounting101.Banking.Reconciliation;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Banking.Reconciliation.Tests;

public sealed class ReconciliationMathTests
{
    private static readonly Guid Cash = Guid.NewGuid();

    private static EntryResponse Entry(Guid id, string direction, decimal amount, string posting = "Posted", string status = "Active") =>
        new(id, 0, new DateOnly(2026, 1, 31), "Standard", status, posting, 1, null, null, null, null,
            [new EntryLineResponse(Cash, direction, amount, new Dictionary<string, Guid>(), null),
             new EntryLineResponse(Guid.NewGuid(), direction == "Debit" ? "Credit" : "Debit", amount, new Dictionary<string, Guid>(), null)]);

    [Fact]
    public void Cash_effect_is_positive_for_a_debit_to_cash_and_negative_for_a_credit()
    {
        Assert.Equal(100m, ReconciliationMath.CashEffect(Entry(Guid.NewGuid(), "Debit", 100m), Cash));
        Assert.Equal(-60m, ReconciliationMath.CashEffect(Entry(Guid.NewGuid(), "Credit", 60m), Cash));
    }

    [Fact]
    public void Cleared_total_sums_only_the_cleared_entries_cash_effects()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
        EntryResponse[] entries = [Entry(a, "Debit", 100m), Entry(b, "Credit", 60m), Entry(c, "Debit", 25m)];
        decimal total = ReconciliationMath.ClearedTotal(entries, new HashSet<Guid> { a, b }, Cash);
        Assert.Equal(40m, total); // +100 − 60
    }

    [Fact]
    public void Reconciled_difference_is_closing_minus_opening_plus_cleared_and_balanced_at_zero()
    {
        // opening 0, cleared +40 → expected closing 40 balances.
        Assert.Equal(0m, ReconciliationMath.ReconciledDifference(0m, 40m, 40m));
        Assert.True(ReconciliationMath.IsBalanced(ReconciliationMath.ReconciledDifference(0m, 40m, 40m)));
        // a $5 bank-only fee not in the cleared total → difference −5, not balanced.
        Assert.Equal(-5m, ReconciliationMath.ReconciledDifference(0m, 35m, 40m));
        Assert.False(ReconciliationMath.IsBalanced(-5m));
    }
}
