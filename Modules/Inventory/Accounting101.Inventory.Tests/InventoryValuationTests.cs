using Accounting101.Inventory;
using Xunit;

public class InventoryValuationTests
{
    [Fact] public void Receipt_reblends_average()
    {
        // 10 @ $2 then 10 @ $4 → 20 on hand, $60 value, $3 avg
        MovementEffect a = InventoryValuation.Receipt(new Valuation(0m, 0m), 10m, 2m);
        MovementEffect b = InventoryValuation.Receipt(new Valuation(a.ResultingOnHand, a.ResultingTotalValue), 10m, 4m);
        Assert.Equal(20m, b.ResultingOnHand);
        Assert.Equal(60m, b.ResultingTotalValue);
        Assert.Equal(40m, b.ExtendedCost);          // 10 × 4
    }

    [Fact] public void Issue_costs_at_current_average()
    {
        // on hand 20 @ $3 avg; issue 5 → COGS 15, remaining 15 @ $45
        MovementEffect e = InventoryValuation.Issue(new Valuation(20m, 60m), 5m);
        Assert.Equal(15m, e.ExtendedCost);
        Assert.Equal(15m, e.ResultingOnHand);
        Assert.Equal(45m, e.ResultingTotalValue);
        Assert.Equal(3m, e.AppliedUnitCost);
    }

    [Fact] public void Full_issue_clears_value_exactly()
    {
        // a rounding-prone average: 3 @ $10 = $30 → avg 10; but make it messy: 3 units, $10.00, then issue 1,1,1
        Valuation v = new(3m, 10m);                  // avg = 3.3333…
        MovementEffect i1 = InventoryValuation.Issue(v, 1m);
        MovementEffect i2 = InventoryValuation.Issue(new Valuation(i1.ResultingOnHand, i1.ResultingTotalValue), 1m);
        MovementEffect i3 = InventoryValuation.Issue(new Valuation(i2.ResultingOnHand, i2.ResultingTotalValue), 1m);
        Assert.Equal(0m, i3.ResultingOnHand);
        Assert.Equal(0m, i3.ResultingTotalValue);    // exact clear on the final unit, no residue
        Assert.Equal(10m, i1.ExtendedCost + i2.ExtendedCost + i3.ExtendedCost);   // COGS totals the value exactly
    }

    [Fact] public void Issue_beyond_on_hand_throws()
    {
        Assert.Throws<InvalidOperationException>(() => InventoryValuation.Issue(new Valuation(2m, 10m), 3m));
    }

    [Fact] public void Overage_adjustment_reblends_like_receipt()
    {
        MovementEffect e = InventoryValuation.Adjustment(new Valuation(10m, 30m), 5m, 4m);   // +5 @ $4
        Assert.Equal(15m, e.ResultingOnHand);
        Assert.Equal(50m, e.ResultingTotalValue);   // 30 + 20
        Assert.Equal(20m, e.ExtendedCost);
    }

    [Fact] public void Shrinkage_adjustment_costs_at_average_and_blocks_negative()
    {
        MovementEffect e = InventoryValuation.Adjustment(new Valuation(10m, 30m), -4m, null);  // -4 @ avg $3
        Assert.Equal(6m, e.ResultingOnHand);
        Assert.Equal(18m, e.ResultingTotalValue);
        Assert.Equal(12m, e.ExtendedCost);
        Assert.Throws<InvalidOperationException>(() => InventoryValuation.Adjustment(new Valuation(2m, 6m), -3m, null));
    }

    [Fact] public void Overage_adjustment_requires_unit_cost()
    {
        Assert.Throws<ArgumentException>(() => InventoryValuation.Adjustment(new Valuation(10m, 30m), 5m, null));
    }
}
