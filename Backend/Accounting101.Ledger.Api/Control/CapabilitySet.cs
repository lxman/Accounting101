using MongoDB.Bson.Serialization.Attributes;

namespace Accounting101.Ledger.Api.Control;

/// <summary>
/// A named, editable bundle of capabilities in the deployment's control database. Sets are the
/// owner-managed successor to the hardcoded <see cref="RolePresets"/>: members reference sets and
/// resolve their capabilities from them (AC-2). Deployment-wide — one catalog per deployment.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class CapabilitySet
{
    [BsonId]
    public Guid Id { get; set; }

    /// <summary>Unique, human-facing name (e.g. "Controller", "Warehouse Clerk").</summary>
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>The capabilities this set grants — each a member of <see cref="Capabilities.All"/>.</summary>
    public IReadOnlyList<string> Capabilities { get; set; } = [];

    /// <summary>True for sets seeded from <see cref="RolePresets"/>: editable in place, but not deletable.</summary>
    public bool Builtin { get; set; }
}
