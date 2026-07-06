using Accounting101.Inventory;

namespace Accounting101.Inventory.Api;

/// <summary>Create or update an item. The valuation fields (Status, OnHandQuantity, TotalValue) are
/// server-owned and never sent.</summary>
public sealed record SaveItemRequest(string Sku, string Name, string? Description, string UnitOfMeasure)
{
    public ItemBody ToBody() => new(Sku, Name, Description, UnitOfMeasure);
}

/// <summary>Record a stock movement (receipt, issue, or adjustment). <see cref="EffectiveDate"/> is
/// required — the clerk always dates a movement.</summary>
public sealed record RecordMovementRequest(
    Guid ItemId, MovementType Type, decimal Quantity, decimal? UnitCost, DateOnly EffectiveDate, string? Memo)
{
    public RecordMovement ToRequest() => new(ItemId, Type, Quantity, UnitCost, EffectiveDate, Memo);
}
