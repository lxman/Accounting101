using MongoDB.Bson.Serialization.Attributes;

namespace Accounting101.Ledger.Api.Platform;

/// <summary>
/// A physical MongoDB cluster the platform can place firms on, keyed by a stable short name (the
/// <see cref="Key"/>, e.g. "default", "cluster-2"). The connection string is resolved through this
/// registry so adding a second Atlas cluster is a data change, not a code change.
/// </summary>
public sealed class ClusterRegistration
{
    [BsonId]
    public string Key { get; set; } = string.Empty;

    public string ConnectionString { get; set; } = string.Empty;
}
