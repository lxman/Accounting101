namespace Accounting101.Inventory;

/// <summary>The stored body of a stock movement (the evidentiary document body). For Receipt and Issue,
/// <see cref="Quantity"/> is a positive magnitude and the direction comes from <see cref="Type"/>. For an
/// Adjustment, <see cref="Type"/> is simply "Adjustment" and the direction lives in the SIGN of
/// <see cref="Quantity"/> (+overage / -shrinkage). <see cref="ExtendedCost"/> is always the positive
/// magnitude posted to the GL. No post-movement valuation snapshot is stored — on-hand and value are
/// derived on read from the ledger fold + movement projection, so a void needs no stored restore.</summary>
public sealed record StockMovementBody(
    Guid ItemId, MovementType Type, DateOnly EffectiveDate, string? Memo,
    decimal Quantity,             // positive for Receipt/Issue; SIGNED for Adjustment (+overage / -shrinkage)
    decimal AppliedUnitCost,      // unit cost actually used
    decimal ExtendedCost);        // positive magnitude posted to the GL

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
    public required MovementStatus Status { get; init; }

    /// <summary>The signed on-hand delta this movement contributes to the quantity projection
    /// (ItemValuationService.ProjectQuantityAsync). Pure Type+Quantity derivation, not stored state.</summary>
    public decimal SignedQuantityEffect => Type switch
    {
        MovementType.Receipt => Quantity,
        MovementType.Issue => -Quantity,
        _ => Quantity,                                  // Adjustment: Quantity is already signed
    };
}

/// <summary>Read model for a stock movement.</summary>
public sealed record StockMovementView(StockMovement Movement);
