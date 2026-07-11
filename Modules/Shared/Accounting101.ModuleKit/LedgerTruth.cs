using Accounting101.Ledger.Contracts;

namespace Accounting101.ModuleKit;

/// <summary>
/// Ledger-truth for a module document's Void state, read from its source journal entries. A document is
/// negated when its <em>primary</em> entry (the one that is not a reversal) has been withdrawn while
/// pending (engine sets its <c>Status</c> to <c>Voided</c>), or when a reversal of that primary exists
/// (<c>ReversalOf</c> points at it). The single shared home of the rule; a service unions it with the
/// document-envelope status so a read can only ever be <em>promoted</em> to Void, never demoted.
/// </summary>
public static class LedgerTruth
{
    public static bool ShowsVoided(IReadOnlyList<EntryResponse> entriesForOneDoc)
    {
        List<EntryResponse> primaries = entriesForOneDoc.Where(e => e.ReversalOf is null).ToList();
        if (primaries.Count == 0) return false;                       // no entry → fall back to envelope
        if (primaries.Any(p => p.Status == "Voided")) return true;    // withdrawn while pending
        HashSet<Guid> primaryIds = primaries.Select(p => p.Id).ToHashSet();
        return entriesForOneDoc.Any(e => e.ReversalOf is { } r && primaryIds.Contains(r)); // reversed
    }
}
