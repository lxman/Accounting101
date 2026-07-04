using MongoDB.Bson.Serialization.Attributes;

namespace Accounting101.Ledger.Api.Platform;

/// <summary>
/// A module's persisted shared secret, stored once in platform_control so it is stable across process
/// restarts and identical across instances. Keyed by the module key. Never logged or surfaced to users.
/// </summary>
public sealed class ModuleSecret
{
    [BsonId]
    public string Key { get; set; } = string.Empty;

    public string Secret { get; set; } = string.Empty;
}
