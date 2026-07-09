using System.Text.Json.Serialization;

namespace Accounting101.Inventory;

/// <summary>Lifecycle of a stock movement: posted, or voided (LIFO).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MovementStatus
{
    Posted = 0,
    Void = 1,
}
