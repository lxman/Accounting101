using Accounting101.Inventory;

namespace Accounting101.Inventory.Api;

/// <summary>Create or update an item. The valuation fields (Status, OnHandQuantity, TotalValue) are
/// server-owned and never sent.</summary>
public sealed record SaveItemRequest(string Sku, string Name, string? Description, string UnitOfMeasure)
{
    public ItemBody ToBody() => new(Sku, Name, Description, UnitOfMeasure);
}
