namespace Accounting101.Inventory;

/// <summary>The stored shape of an item — the opaque reference-document body. The item id is the document
/// id, so it is not repeated here. Valuation (on-hand/value) is NOT stored: it is derived on read from the
/// ledger fold + movement projection (see ItemValuationService), so the document carries only editable
/// register fields. Status is NOT stored here either — it is derived from the document's DocumentLifecycle.</summary>
public sealed record ItemDocument(
    string Sku,
    string Name,
    string? Description,
    string UnitOfMeasure);
