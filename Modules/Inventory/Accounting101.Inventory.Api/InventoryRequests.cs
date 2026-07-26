using Accounting101.Inventory;

namespace Accounting101.Inventory.Api;

/// <summary>Create or update an item. The valuation fields (Status, OnHandQuantity, TotalValue) are
/// server-owned and never sent.</summary>
public sealed record SaveItemRequest(string Sku, string Name, string? Description, string UnitOfMeasure)
{
    public ItemBody ToBody() => new(Sku, Name, Description, UnitOfMeasure);
}

/// <summary>Record a stock movement (receipt, issue, or adjustment). <see cref="EffectiveDate"/> is
/// required — the clerk always dates a movement. <paramref name="Id"/> is an optional caller-supplied
/// retry key that becomes the movement's id: the same key with the same content replays (200, no second
/// movement and no second valuation effect).
///
/// Reusing a key for different content is a 422 only where the difference reaches the composed GL entry.
/// It always does for a different <see cref="EffectiveDate"/>, <see cref="Type"/> or <see cref="ItemId"/>,
/// and for a different <see cref="Quantity"/> or <see cref="UnitCost"/> on a receipt or an overage
/// adjustment. It does NOT for a different quantity on an ISSUE or a SHRINKAGE adjustment: those cost at
/// the folded weighted average, so a replay reuses the stored cost and the entry is unchanged — the
/// ORIGINAL movement is returned with 200 rather than rejected. Reuse a key only for a true retry of the
/// same operation.</summary>
public sealed record RecordMovementRequest(
    Guid ItemId, MovementType Type, decimal Quantity, decimal? UnitCost, DateOnly EffectiveDate, string? Memo,
    Guid? Id = null)
{
    public RecordMovement ToRequest() => new(ItemId, Type, Quantity, UnitCost, EffectiveDate, Memo, Id);
}

/// <summary>Optional free-text reason accompanying a void.</summary>
public sealed record VoidReasonRequest(string? Reason);
