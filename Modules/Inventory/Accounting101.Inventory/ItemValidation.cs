namespace Accounting101.Inventory;

/// <summary>Pure validation of an item body. Returns null when valid, else a human-readable reason
/// (surfaced as 422 at the endpoint).</summary>
public static class ItemValidation
{
    public static string? Validate(ItemBody body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (string.IsNullOrWhiteSpace(body.Sku)) return "Sku is required.";
        if (string.IsNullOrWhiteSpace(body.Name)) return "Name is required.";
        if (string.IsNullOrWhiteSpace(body.UnitOfMeasure)) return "UnitOfMeasure is required.";
        return null;
    }
}
