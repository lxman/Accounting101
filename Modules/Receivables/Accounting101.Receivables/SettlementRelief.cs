using Accounting101.Ledger.Contracts;

namespace Accounting101.Receivables;

/// <summary>Folds one settlement document's AR relief directly from its own ledger entry — what
/// "Allocated"/"Total"/"Applied" meant when the module stored an <c>Allocation[]</c> per document. The
/// module now stores no amounts for the per-invoice split; the ledger entry the recipe posted for this
/// <paramref name="sourceRef"/> is the only place that total lives.
/// <para>
/// Finds the document's ORIGINAL entry (the one that is not itself a reversal) regardless of its current
/// approval or void state, and sums its lines on the Receivable account. This deliberately does not gate on
/// Posted/approval — before this fold replaced the stored array, these figures were always available the
/// instant the document was recorded, and callers (e.g. the negative-credit guard on payment void) still
/// need that immediacy.
/// </para></summary>
internal static class SettlementRelief
{
    public static async Task<decimal> ForSourceAsync(
        ILedgerClient ledger, Guid clientId, Guid sourceRef, Guid receivableAccountId, CancellationToken ct)
    {
        IReadOnlyList<EntryResponse> entries = await ledger.GetEntriesBySourceRefAsync(clientId, sourceRef, ct);
        EntryResponse? entry = entries.FirstOrDefault(e => e.ReversalOf is null);
        return entry?.Lines.Where(l => l.AccountId == receivableAccountId).Sum(l => l.Amount) ?? 0m;
    }
}
