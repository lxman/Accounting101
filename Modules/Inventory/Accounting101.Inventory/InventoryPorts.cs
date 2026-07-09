using Accounting101.Ledger.Contracts;

namespace Accounting101.Inventory;

/// <summary>The module's item register store — reference documents backed by the engine's document store.
/// Create/update/deactivate lifecycle; the module owns no database connection.</summary>
public interface IItemStore
{
    Task<Item> CreateAsync(Guid clientId, ItemBody body, CancellationToken ct = default);
    Task<UpdateResult> UpdateAsync(Guid clientId, Guid itemId, ItemBody body, CancellationToken ct = default);
    Task<DeactivateResult> DeactivateAsync(Guid clientId, Guid itemId, CancellationToken ct = default);
    Task<ReactivateResult> ReactivateAsync(Guid clientId, Guid itemId, CancellationToken ct = default);
    Task<Item?> GetAsync(Guid clientId, Guid itemId, CancellationToken ct = default);
    Task<Item?> GetBySkuAsync(Guid clientId, string sku, CancellationToken ct = default);
    Task<PagedResponse<Item>> GetByClientPagedAsync(
        Guid clientId, int skip, int limit, bool descending, bool includeInactive, CancellationToken ct = default);

    /// <summary>Overwrite the item's server-owned valuation (on-hand + total value). Used by movements.</summary>
    Task SetValuationAsync(Guid clientId, Guid itemId, decimal onHand, decimal totalValue, CancellationToken ct = default);
}

/// <summary>Outcome of an item update: not found, refused because inactive (reactivate first), duplicate
/// Sku on rename, or the updated item.</summary>
public enum UpdateOutcome { NotFound, Inactive, Updated, DuplicateSku }

public readonly record struct UpdateResult(UpdateOutcome Outcome, Item? Item)
{
    public static readonly UpdateResult NotFound = new(UpdateOutcome.NotFound, null);
    public static readonly UpdateResult Inactive = new(UpdateOutcome.Inactive, null);
    public static readonly UpdateResult DuplicateSku = new(UpdateOutcome.DuplicateSku, null);
    public static UpdateResult Updated(Item item) => new(UpdateOutcome.Updated, item);
}

/// <summary>Outcome of a deactivate: the item was not found, was already inactive, has stock on hand
/// (blocked), or was deactivated now.</summary>
public enum DeactivateResult { NotFound, AlreadyInactive, Deactivated, HasStock }

public enum ReactivateResult { NotFound, AlreadyActive, Reactivated }

/// <summary>The module's stock-movement store — numbered, append-only evidentiary documents backed by
/// the engine's document store (created, immediately finalized, voidable). The module owns no database
/// connection.</summary>
public interface IStockMovementStore
{
    Task<StockMovement> RecordAsync(Guid clientId, StockMovementBody body, CancellationToken ct = default);
    Task VoidAsync(Guid clientId, Guid movementId, CancellationToken ct = default);
    Task<StockMovement?> GetAsync(Guid clientId, Guid movementId, CancellationToken ct = default);
    Task<PagedResponse<StockMovement>> GetByItemPagedAsync(
        Guid clientId, Guid itemId, int skip, int limit, bool descending, bool includeVoided, CancellationToken ct = default);

    /// <summary>The most-recent non-voided movement for the given item (not any other item) — the LIFO
    /// void's target.</summary>
    Task<StockMovement?> GetLatestForItemAsync(Guid clientId, Guid itemId, CancellationToken ct = default);
}
