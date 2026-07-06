namespace Accounting101.Inventory;

/// <summary>The stored body of a stock movement (the evidentiary document body). Quantity/UnitCost/Value are
/// always non-negative magnitudes; the movement's direction comes from <see cref="Type"/>. Resulting*
/// fields are a snapshot of the item's valuation immediately after this movement was applied — they let a
/// void reconstruct the prior valuation without replaying every earlier movement.</summary>
public sealed record StockMovementBody(
    Guid ItemId,
    MovementType Type,
    decimal Quantity,
    decimal UnitCost,
    decimal Value,
    decimal ResultingOnHandQuantity,
    decimal ResultingTotalValue,
    DateOnly? EffectiveDate = null,
    string? Memo = null);

/// <summary>A stock movement — the engine assigns the number; status is derived from the document
/// lifecycle. Every field from <see cref="StockMovementBody"/> is copied onto this view alongside the
/// engine-owned Id/Number/Status.</summary>
public sealed record StockMovement
{
    public required Guid Id { get; init; }
    public string? Number { get; init; }
    public required Guid ItemId { get; init; }
    public required MovementType Type { get; init; }
    public required decimal Quantity { get; init; }
    public required decimal UnitCost { get; init; }
    public required decimal Value { get; init; }
    public required decimal ResultingOnHandQuantity { get; init; }
    public required decimal ResultingTotalValue { get; init; }
    public DateOnly? EffectiveDate { get; init; }
    public string? Memo { get; init; }
    public required MovementStatus Status { get; init; }

    /// <summary>Quantity signed for its effect on on-hand quantity: positive for a receipt or an
    /// (already-signed) adjustment, negative for an issue. Used by the LIFO void to reverse the effect.</summary>
    public decimal SignedQuantityEffect => Type == MovementType.Issue ? -Quantity : Quantity;

    /// <summary>Value signed for its effect on total inventory value, mirroring <see cref="SignedQuantityEffect"/>.</summary>
    public decimal SignedValueEffect => Type == MovementType.Issue ? -Value : Value;
}

/// <summary>Read model for a stock movement.</summary>
public sealed record StockMovementView(StockMovement Movement);
