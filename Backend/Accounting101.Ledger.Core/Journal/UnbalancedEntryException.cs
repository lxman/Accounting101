namespace Accounting101.Ledger.Core.Journal;

/// <summary>
/// Thrown when an attempt is made to create a journal entry whose lines do not
/// net to zero. <see cref="Imbalance"/> is the debit-positive remainder
/// (debits minus credits).
/// </summary>
public sealed class UnbalancedEntryException(decimal imbalance)
    : Exception($"Journal entry does not balance: debits minus credits = {imbalance}.")
{
    public decimal Imbalance { get; } = imbalance;
}
