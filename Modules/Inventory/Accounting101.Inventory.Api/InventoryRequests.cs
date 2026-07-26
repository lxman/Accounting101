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
/// retry key that becomes the movement's id: same key and same content replays (200, no second movement
/// and no second valuation effect), different content is a 422.</summary>
public sealed record RecordMovementRequest(
    Guid ItemId, MovementType Type, decimal Quantity, decimal? UnitCost, DateOnly EffectiveDate, string? Memo,
    Guid? Id = null)
{
    public RecordMovement ToRequest() => new(ItemId, Type, Quantity, UnitCost, EffectiveDate, Memo, Id);
}

/// <summary>Optional free-text reason accompanying a void.</summary>
public sealed record VoidReasonRequest(string? Reason);
