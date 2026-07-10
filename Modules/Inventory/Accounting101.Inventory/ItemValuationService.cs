using Accounting101.Ledger.Contracts;

namespace Accounting101.Inventory;

/// <summary>An item's derived on-hand quantity and carried value — value from the {Item} fold of the
/// Inventory account (debit-normal → positive), quantity from the movement documents' signed quantity.</summary>
public readonly record struct ItemValuation(decimal OnHand, decimal TotalValue)
{
    public decimal AverageUnitCost => OnHand == 0m ? 0m : TotalValue / OnHand;
}

/// <summary>Computes an item's valuation from the ledger fold + movement projection, both keyed off
/// entry-on-books so subledger and GL cannot diverge. Reads pass <c>includePending: false</c> (posted-only);
/// writes (next-issue cost, block-negative) pass <c>true</c> — the SAME gate for value and quantity so the
/// weighted-average ratio stays coherent.</summary>
public sealed class ItemValuationService(
    IStockMovementStore movements, IInventoryAccountsProvider accounts, ILedgerClient ledger)
{
    public const string ItemDimension = "Item";

    public async Task<ItemValuation> GetAsync(Guid clientId, Guid itemId, bool includePending, CancellationToken ct = default)
    {
        InventoryPostingAccounts acct = await accounts.GetAccountsAsync(clientId, ct);
        IReadOnlyList<SubledgerLineResponse> lines =
            await ledger.GetSubledgerAsync(clientId, acct.InventoryAssetAccountId, ItemDimension, null, ct, includePending);
        // Inventory is debit-normal → the debit-positive fold reads POSITIVE. No negation (unlike AR/FA).
        decimal value = lines.Where(l => l.DimensionValue == itemId).Sum(l => l.Balance);
        decimal onHand = await ProjectQuantityAsync(clientId, itemId, includePending, ct);
        return new ItemValuation(onHand, value);
    }

    private async Task<decimal> ProjectQuantityAsync(Guid clientId, Guid itemId, bool includePending, CancellationToken ct)
    {
        IReadOnlyList<StockMovement> all = await movements.GetAllByItemAsync(clientId, itemId, ct);
        if (all.Count == 0) return 0m;
        IReadOnlyList<EntryResponse> entries =
            await ledger.GetEntriesBySourceRefsAsync(clientId, all.Select(m => m.Id).ToList(), ct);
        ILookup<Guid, EntryResponse> bySource = entries.Where(e => e.SourceRef is not null).ToLookup(e => e.SourceRef!.Value);

        decimal onHand = 0m;
        foreach (StockMovement m in all)
            if (OnBooks(bySource[m.Id], includePending))
                onHand += m.SignedQuantityEffect;
        return onHand;
    }

    /// <summary>A movement counts iff its spawned primary entry is on the books under THIS gate, and no
    /// reversal of it is on the books under the SAME gate. The reversal check is gated on posting exactly
    /// like the primary — because the value fold nets a reversal's lines under the same posting rule, so
    /// gating both axes identically keeps value and quantity coherent in the void→reversal-approval window
    /// (a merely-pending reversal must not drop the quantity while the posted-only value fold still counts it).</summary>
    private static bool OnBooks(IEnumerable<EntryResponse> forSource, bool includePending)
    {
        List<EntryResponse> list = forSource.ToList();
        EntryResponse? primary = list.FirstOrDefault(e =>
            e is { Status: "Active", ReversalOf: null } && (includePending || e.Posting == "Posted"));
        if (primary is null) return false;                                            // no on-books primary
        bool reversed = list.Any(e => e.ReversalOf == primary.Id && (includePending || e.Posting == "Posted"));
        return !reversed;                                                             // reversed under the same gate → off the books
    }
}
