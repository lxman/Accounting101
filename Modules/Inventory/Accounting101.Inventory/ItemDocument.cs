namespace Accounting101.Inventory;

/// <summary>The stored shape of an item — the opaque reference-document body. The item id is the document
/// id, so it is not repeated here. OnHandQuantity/TotalValue are server-owned (movements own them via
/// SetValuationAsync); Status is NOT stored here — it is derived from the document's DocumentLifecycle.</summary>
public sealed record ItemDocument(
    string Sku,
    string Name,
    string? Description,
    string UnitOfMeasure,
    decimal OnHandQuantity,
    decimal TotalValue);
