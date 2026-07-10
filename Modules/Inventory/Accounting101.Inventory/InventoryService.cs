using Accounting101.Ledger.Contracts;

namespace Accounting101.Inventory;

/// <summary>The item-register lifecycle: validate then create / update, deactivate, reactivate, get.
/// Validation failures throw ArgumentException (→ 422 at the endpoint). Sku uniqueness on create throws
/// InvalidOperationException (→ 409 at the endpoint); on update it is enforced by the store (DuplicateSku
/// outcome) since the existing item must be excluded from the match. Every read overlays the item's
/// OnHandQuantity/TotalValue with the posted-only ledger fold (via ItemValuationService) — the stored
/// fields on the document are write-only from here on (still written by the movement service; deleted in
/// a later task).</summary>
public sealed class InventoryService(IItemStore store, ItemValuationService valuation)
{
    public async Task<Item> CreateAsync(Guid clientId, ItemBody body, CancellationToken ct = default)
    {
        if (ItemValidation.Validate(body) is { } error) throw new ArgumentException(error);
        if (await store.GetBySkuAsync(clientId, body.Sku, ct) is not null)
            throw new InvalidOperationException($"An item with Sku '{body.Sku}' already exists.");
        return await store.CreateAsync(clientId, body, ct);
    }

    public Task<UpdateResult> UpdateAsync(Guid clientId, Guid itemId, ItemBody body, CancellationToken ct = default)
    {
        if (ItemValidation.Validate(body) is { } error) throw new ArgumentException(error);
        return store.UpdateAsync(clientId, itemId, body, ct);
    }

    public async Task<DeactivateResult> DeactivateAsync(Guid clientId, Guid itemId, CancellationToken ct = default)
    {
        // Has-stock guard reads the posted-only projection (the store no longer stores on-hand).
        ItemValuation v = await valuation.GetAsync(clientId, itemId, includePending: false, ct);
        if (v.OnHand != 0m && await store.GetAsync(clientId, itemId, ct) is not null)
            return DeactivateResult.HasStock;
        return await store.DeactivateAsync(clientId, itemId, ct);
    }

    public Task<ReactivateResult> ReactivateAsync(Guid clientId, Guid itemId, CancellationToken ct = default) =>
        store.ReactivateAsync(clientId, itemId, ct);

    public async Task<Item?> GetAsync(Guid clientId, Guid itemId, CancellationToken ct = default)
    {
        Item? item = await store.GetAsync(clientId, itemId, ct);
        return item is null ? null : await WithValuationAsync(clientId, item, ct);
    }

    public async Task<PagedResponse<Item>> GetPagedAsync(
        Guid clientId, int skip, int limit, bool descending, bool includeInactive, CancellationToken ct = default)
    {
        PagedResponse<Item> page = await store.GetByClientPagedAsync(clientId, skip, limit, descending, includeInactive, ct);
        // ONE batched valuation call for the whole page (constant ledger calls), not one per item.
        IReadOnlyDictionary<Guid, ItemValuation> valuations =
            await valuation.GetManyAsync(clientId, page.Items.Select(i => i.Id).ToList(), includePending: false, ct);
        List<Item> folded = page.Items
            .Select(item =>
            {
                ItemValuation v = valuations.GetValueOrDefault(item.Id);
                return item with { OnHandQuantity = v.OnHand, TotalValue = v.TotalValue };
            })
            .ToList();
        return new PagedResponse<Item>(folded, page.Total, page.Skip, page.Limit);
    }

    private async Task<Item> WithValuationAsync(Guid clientId, Item item, CancellationToken ct)
    {
        ItemValuation v = await valuation.GetAsync(clientId, item.Id, includePending: false, ct);
        return item with { OnHandQuantity = v.OnHand, TotalValue = v.TotalValue };
    }
}
