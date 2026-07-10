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

    /// <summary>Batch form of <see cref="GetAsync"/> for a page of items: a CONSTANT number of ledger calls
    /// (one subledger fold + one entry-status batch read) regardless of page size, instead of the N+1 shape
    /// of calling <see cref="GetAsync"/> once per item. Shares the exact same <see cref="OnBooks"/> gating
    /// logic as the single-item path so the two never diverge. Items with no lines/movements fold to
    /// <c>(0, 0)</c> (absent from the fold rather than present-and-zero, so the caller should treat a
    /// missing key as zero).</summary>
    public async Task<IReadOnlyDictionary<Guid, ItemValuation>> GetManyAsync(
        Guid clientId, IReadOnlyList<Guid> itemIds, bool includePending, CancellationToken ct = default)
    {
        if (itemIds.Count == 0) return new Dictionary<Guid, ItemValuation>();

        InventoryPostingAccounts acct = await accounts.GetAccountsAsync(clientId, ct);
        // ONE subledger fold for the whole page, grouped by item (dimension value) — not one per item.
        IReadOnlyList<SubledgerLineResponse> lines =
            await ledger.GetSubledgerAsync(clientId, acct.InventoryAssetAccountId, ItemDimension, null, ct, includePending);
        Dictionary<Guid, decimal> valueByItem = lines
            .GroupBy(l => l.DimensionValue)
            .ToDictionary(g => g.Key, g => g.Sum(l => l.Balance));

        // ONE movement scan for all the page's items, then ONE batched entry-status read across all of them.
        IReadOnlyList<StockMovement> all = await movements.GetAllByItemsAsync(clientId, itemIds, ct);
        ILookup<Guid, StockMovement> movementsByItem = all.ToLookup(m => m.ItemId);
        ILookup<Guid, EntryResponse> bySource = await ResolveBySourceAsync(clientId, all, ct);

        Dictionary<Guid, ItemValuation> result = new();
        foreach (Guid itemId in itemIds)
        {
            decimal onHand = ProjectOnHand(movementsByItem[itemId], bySource, includePending);
            result[itemId] = new ItemValuation(onHand, valueByItem.GetValueOrDefault(itemId));
        }
        return result;
    }

    private async Task<decimal> ProjectQuantityAsync(Guid clientId, Guid itemId, bool includePending, CancellationToken ct)
    {
        IReadOnlyList<StockMovement> all = await movements.GetAllByItemAsync(clientId, itemId, ct);
        if (all.Count == 0) return 0m;
        ILookup<Guid, EntryResponse> bySource = await ResolveBySourceAsync(clientId, all, ct);
        return ProjectOnHand(all, bySource, includePending);
    }

    /// <summary>Batches entry-status lookup for a set of movements into ONE ledger call (or zero calls when
    /// there are no movements), keyed by source ref for O(1) per-movement lookup.</summary>
    private async Task<ILookup<Guid, EntryResponse>> ResolveBySourceAsync(
        Guid clientId, IReadOnlyList<StockMovement> forMovements, CancellationToken ct)
    {
        if (forMovements.Count == 0) return Enumerable.Empty<EntryResponse>().ToLookup(e => e.SourceRef!.Value);
        IReadOnlyList<EntryResponse> entries =
            await ledger.GetEntriesBySourceRefsAsync(clientId, forMovements.Select(m => m.Id).ToList(), ct);
        return entries.Where(e => e.SourceRef is not null).ToLookup(e => e.SourceRef!.Value);
    }

    /// <summary>Sums the signed quantity effect of every movement in <paramref name="forItem"/> whose
    /// spawned entry is on the books under the gate — the SAME projection used by both the single-item and
    /// batch paths, so they cannot diverge.</summary>
    private static decimal ProjectOnHand(
        IEnumerable<StockMovement> forItem, ILookup<Guid, EntryResponse> bySource, bool includePending)
    {
        decimal onHand = 0m;
        foreach (StockMovement m in forItem)
            if (OnBooks(bySource[m.Id], includePending))
                onHand += m.SignedQuantityEffect;
        return onHand;
    }

    /// <summary>A movement counts iff its spawned primary entry is on the books under THIS gate, and no
    /// reversal of it is on the books under the SAME gate. The reversal check is gated on posting exactly
    /// like the primary — because the value fold nets a reversal's lines under the same posting rule, so
    /// gating both axes identically keeps value and quantity coherent in the void→reversal-approval window
    /// (a merely-pending reversal must not drop the quantity while the posted-only value fold still counts it).
    /// The reversal must also be Active — a VOIDED reversal cannot drop the movement while the value fold
    /// (which excludes non-Active entries) would still count the primary; requiring Active on both sides
    /// keeps value and quantity symmetric.</summary>
    private static bool OnBooks(IEnumerable<EntryResponse> forSource, bool includePending)
    {
        List<EntryResponse> list = forSource.ToList();
        EntryResponse? primary = list.FirstOrDefault(e =>
            e is { Status: "Active", ReversalOf: null } && (includePending || e.Posting == "Posted"));
        if (primary is null) return false;                                            // no on-books primary
        bool reversed = list.Any(e =>
            e is { Status: "Active" } && e.ReversalOf == primary.Id && (includePending || e.Posting == "Posted"));
        return !reversed;                                                             // reversed under the same gate → off the books
    }
}
