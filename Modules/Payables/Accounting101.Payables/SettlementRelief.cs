using Accounting101.Ledger.Contracts;

namespace Accounting101.Payables;

/// <summary>Folds one settlement document's A/P relief from its own ledger entry — what "Allocated"/"Applied"
/// meant when the module stored an Allocation[]. Sums the entry's lines on the Payable account. When
/// <paramref name="postedOnly"/> is true a not-yet-Posted entry contributes 0 (read surfaces); when false the
/// relief is immediate (the payment-void negative-credit guard needs it before approval).</summary>
internal static class SettlementRelief
{
    public static async Task<decimal> ForSourceAsync(
        ILedgerClient ledger, Guid clientId, Guid sourceRef, Guid payableAccountId, CancellationToken ct, bool postedOnly)
    {
        IReadOnlyList<EntryResponse> entries = await ledger.GetEntriesBySourceRefAsync(clientId, sourceRef, ct);
        EntryResponse? entry = entries.FirstOrDefault(e => e.ReversalOf is null);
        if (entry is null) return 0m;
        if (postedOnly && entry.Posting != "Posted") return 0m;
        return entry.Lines.Where(l => l.AccountId == payableAccountId).Sum(l => l.Amount);
    }
}
