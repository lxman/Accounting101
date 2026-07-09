using System.Text.Json.Serialization;

namespace Accounting101.Inventory;

/// <summary>The type of stock movement: receipt into inventory, issue from inventory, or inventory adjustment.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MovementType
{
    /// <summary>Receipt of inventory from vendor or manufacturing.</summary>
    Receipt,

    /// <summary>Issue (sale or consumption) from inventory.</summary>
    Issue,

    /// <summary>Inventory adjustment (count variance, shrinkage, or overage).</summary>
    Adjustment,
}
