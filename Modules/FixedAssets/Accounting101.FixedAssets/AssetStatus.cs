using System.Text.Json.Serialization;

namespace Accounting101.FixedAssets;

/// <summary>Register lifecycle of an asset. New assets are Active; FA-3 disposal sets Disposed. FA-1 never
/// sets Disposed. 0-default so a legacy document reads as Active.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AssetStatus
{
    Active = 0,
    Disposed = 1,
}
