namespace Accounting101.Inventory;

public readonly record struct Valuation(decimal OnHand, decimal TotalValue)
{
    public decimal AverageUnitCost => OnHand == 0m ? 0m : TotalValue / OnHand;
}

public readonly record struct MovementEffect(
    decimal AppliedUnitCost, decimal ExtendedCost, decimal ResultingOnHand, decimal ResultingTotalValue);

/// <summary>Pure weighted-average costing over a carried (OnHand, TotalValue) pair. GL amounts are
/// cents, banker's-rounded. A full issue clears value exactly (no rounding residue). Blocks any move
/// that would drive on-hand below zero.</summary>
public static class InventoryValuation
{
    private static decimal Round(decimal v) => Math.Round(v, 2, MidpointRounding.ToEven);

    public static MovementEffect Receipt(Valuation current, decimal quantity, decimal unitCost)
    {
        if (quantity <= 0m) throw new ArgumentException("Receipt quantity must be positive.", nameof(quantity));
        if (unitCost < 0m) throw new ArgumentException("Unit cost must not be negative.", nameof(unitCost));
        decimal ext = Round(quantity * unitCost);
        return new MovementEffect(unitCost, ext, current.OnHand + quantity, current.TotalValue + ext);
    }

    public static MovementEffect Issue(Valuation current, decimal quantity)
    {
        if (quantity <= 0m) throw new ArgumentException("Issue quantity must be positive.", nameof(quantity));
        if (quantity > current.OnHand)
            throw new InvalidOperationException("Issue would drive on-hand below zero.");
        decimal avg = current.AverageUnitCost;
        decimal cost = quantity == current.OnHand ? current.TotalValue : Round(quantity * avg);
        return new MovementEffect(avg, cost, current.OnHand - quantity, current.TotalValue - cost);
    }

    public static MovementEffect Adjustment(Valuation current, decimal signedQuantity, decimal? unitCost)
    {
        if (signedQuantity == 0m) throw new ArgumentException("Adjustment quantity must be non-zero.", nameof(signedQuantity));
        if (signedQuantity > 0m)   // overage — behaves like a receipt at the provided cost
        {
            if (unitCost is not { } cost) throw new ArgumentException("An increase adjustment requires a unit cost.", nameof(unitCost));
            if (cost < 0m) throw new ArgumentException("Unit cost must not be negative.", nameof(unitCost));
            decimal ext = Round(signedQuantity * cost);
            return new MovementEffect(cost, ext, current.OnHand + signedQuantity, current.TotalValue + ext);
        }
        decimal decrease = -signedQuantity;   // shrinkage — costs at current average
        if (decrease > current.OnHand)
            throw new InvalidOperationException("Adjustment would drive on-hand below zero.");
        decimal avg = current.AverageUnitCost;
        decimal shrink = decrease == current.OnHand ? current.TotalValue : Round(decrease * avg);
        return new MovementEffect(avg, shrink, current.OnHand - decrease, current.TotalValue - shrink);
    }
}
