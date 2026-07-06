namespace Accounting101.Inventory;

/// <summary>The item-register lifecycle: validate then create / update, deactivate, reactivate, get.
/// Validation failures throw ArgumentException (→ 422 at the endpoint). Sku uniqueness on create throws
/// InvalidOperationException (→ 409 at the endpoint); on update it is enforced by the store (DuplicateSku
/// outcome) since the existing item must be excluded from the match.</summary>
public sealed class InventoryService(IItemStore store)
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

    public Task<DeactivateResult> DeactivateAsync(Guid clientId, Guid itemId, CancellationToken ct = default) =>
        store.DeactivateAsync(clientId, itemId, ct);

    public Task<ReactivateResult> ReactivateAsync(Guid clientId, Guid itemId, CancellationToken ct = default) =>
        store.ReactivateAsync(clientId, itemId, ct);

    public Task<Item?> GetAsync(Guid clientId, Guid itemId, CancellationToken ct = default) =>
        store.GetAsync(clientId, itemId, ct);
}
