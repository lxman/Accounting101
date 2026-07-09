namespace Accounting101.Inventory;

/// <summary>An item in the register. Its Id is the reference-document id.</summary>
public sealed record Item
{
    public required Guid Id { get; init; }
    public required string Sku { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string UnitOfMeasure { get; init; }
    public required ItemStatus Status { get; init; }
    public required decimal OnHandQuantity { get; init; }
    public required decimal TotalValue { get; init; }
}
