namespace Accounting101.Inventory;

/// <summary>The editable parameters of an item (create/update input). Status and the valuation fields
/// (OnHandQuantity/TotalValue) are server-owned and are NOT part of the body.</summary>
public sealed record ItemBody(
    string Sku,
    string Name,
    string? Description,
    string UnitOfMeasure);
