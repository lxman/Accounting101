namespace Accounting101.Inventory;

/// <summary>The stored body of a stock movement (the evidentiary document body). For Receipt and Issue,
/// <see cref="Quantity"/> is a positive magnitude and the direction comes from <see cref="Type"/>. For an
/// Adjustment, <see cref="Type"/> is simply "Adjustment" and the direction lives in the SIGN of
/// <see cref="Quantity"/> (+overage / -shrinkage). <see cref="ExtendedCost"/> is always the positive
/// magnitude posted to the GL. <see cref="ResultingOnHand"/>/<see cref="ResultingTotalValue"/> snapshot the
/// item's valuation immediately after this movement was applied — they let a void reconstruct the prior
/// valuation without replaying every earlier movement.</summary>
public sealed record StockMovementBody(
    Guid ItemId, MovementType Type, DateOnly EffectiveDate, string? Memo,
    decimal Quantity,             // positive for Receipt/Issue; SIGNED for Adjustment (+overage / -shrinkage)
    decimal AppliedUnitCost,      // unit cost actually used
    decimal ExtendedCost,         // positive magnitude posted to the GL
    decimal ResultingOnHand, decimal ResultingTotalValue);

/// <summary>A stock movement — the engine assigns the number; status is derived from the document
/// lifecycle. Every field from <see cref="StockMovementBody"/> is copied onto this view alongside the
/// engine-owned Id/Number/Status.</summary>
public sealed record StockMovement
{
    public required Guid Id { get; init; }
    public string? Number { get; init; }               // MV-#####
    public required Guid ItemId { get; init; }
    public required MovementType Type { get; init; }
    public required DateOnly EffectiveDate { get; init; }   // REQUIRED, not nullable
    public string? Memo { get; init; }
    public required decimal Quantity { get; init; }
    public required decimal AppliedUnitCost { get; init; }
    public required decimal ExtendedCost { get; init; }
    public required decimal ResultingOnHand { get; init; }
    public required decimal ResultingTotalValue { get; init; }
    public required MovementStatus Status { get; init; }

    /// <summary>The net change this movement applied to the item's on-hand quantity (for void reversal).</summary>
    public decimal SignedQuantityEffect => Type switch
    {
        MovementType.Receipt => Quantity,
        MovementType.Issue => -Quantity,
        _ => Quantity,                                  // Adjustment: Quantity is already signed
    };

    /// <summary>The net change this movement applied to the item's total value (for void reversal).</summary>
    public decimal SignedValueEffect => Type switch
    {
        MovementType.Receipt => ExtendedCost,
        MovementType.Issue => -ExtendedCost,
        _ => Quantity >= 0m ? ExtendedCost : -ExtendedCost,  // Adjustment: overage adds, shrinkage subtracts
    };
}

/// <summary>Read model for a stock movement.</summary>
public sealed record StockMovementView(StockMovement Movement);
