namespace Accounting101.Inventory;

/// <summary>Read model for an item — the register record plus its average unit cost (total value / on-hand
/// quantity, or zero when there is no stock), a convenience for callers.</summary>
public sealed record ItemView(Item Item)
{
    public decimal AverageUnitCost => Item.OnHandQuantity == 0m ? 0m : Item.TotalValue / Item.OnHandQuantity;
}
