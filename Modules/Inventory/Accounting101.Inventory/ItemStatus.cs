using System.Text.Json.Serialization;

namespace Accounting101.Inventory;

/// <summary>Register lifecycle of an item, derived from the underlying reference document's
/// DocumentLifecycle (Active/Inactive) — not a field the store persists directly.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ItemStatus
{
    Active = 0,
    Inactive = 1,
}
