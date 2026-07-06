namespace Accounting101.Inventory;

/// <summary>Read model for an item — the register record plus its average unit cost (total value / on-hand
/// quantity), a convenience for callers.</summary>
public sealed record ItemView(Item Item)
{
    public decimal? AverageUnitCost => Item.OnHandQuantity == 0m ? null : Item.TotalValue / Item.OnHandQuantity;
}
