using Accounting101.Ledger.Contracts;

namespace Accounting101.Receivables;

/// <summary>Folds one settlement document's AR relief directly from its own ledger entry — what
/// "Allocated"/"Total"/"Applied" meant when the module stored an <c>Allocation[]</c> per document. The
/// module now stores no amounts for the per-invoice split; the ledger entry the recipe posted for this
/// <paramref name="sourceRef"/> is the only place that total lives.
/// <para>
/// Finds the document's ORIGINAL entry (the one that is not itself a reversal) regardless of its current
/// void state, and sums its lines on the Receivable account. Two modes, selected by
/// <paramref name="postedOnly"/>:
/// </para>
/// <para>
/// <c>postedOnly: true</c> — the entry only contributes once it is Posted (approved); a PendingApproval
/// entry contributes 0. Required by every READ surface (customer statement, credit activity, credits list)
/// so a document's relief never appears before its own posting does — matching the Posted-only
/// <c>ArBalance</c> it must reconcile against.
/// </para>
/// <para>
/// <c>postedOnly: false</c> (default) — the immediate legacy behavior: the figure is available the instant
/// the document is recorded, regardless of approval state. Reserved for the payment-void negative-credit
/// guard, which needs that immediacy to reject a void that would double up an already-consumed
/// overpayment credit.
/// </para></summary>
internal static class SettlementRelief
{
    public static async Task<decimal> ForSourceAsync(
        ILedgerClient ledger, Guid clientId, Guid sourceRef, Guid receivableAccountId, CancellationToken ct,
        bool postedOnly = false)
    {
        IReadOnlyList<EntryResponse> entries = await ledger.GetEntriesBySourceRefAsync(clientId, sourceRef, ct);
        EntryResponse? entry = entries.FirstOrDefault(e => e.ReversalOf is null);
        if (entry is null) return 0m;
        if (postedOnly && entry.Posting != "Posted") return 0m;
        return entry.Lines.Where(l => l.AccountId == receivableAccountId).Sum(l => l.Amount);
    }
}
