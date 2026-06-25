namespace Accounting101.Ledger.Core.Journal;

/// <summary>
/// A period close was refused because entries dated in that period are still awaiting approval — closing
/// would strand them (a closed period can no longer be posted to or voided). Carries the blockers so the
/// caller can list exactly what to approve or void before retrying.
/// </summary>
public sealed class PeriodCloseBlockedException(IReadOnlyList<JournalEntry> blockers)
    : InvalidOperationException(
        $"Cannot close: {blockers.Count} entr{(blockers.Count == 1 ? "y is" : "ies are")} " +
        "dated in this period and still awaiting approval. Approve or void them, then close.")
{
    public IReadOnlyList<JournalEntry> Blockers { get; } = blockers;
}
