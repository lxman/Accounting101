using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Accounting101.Ledger.Api.Control;

/// <summary>
/// Grants a user authority on a client's books. The authoritative grant is <see cref="Capabilities"/>
/// (a per-member set); <see cref="GrantedRoles"/> records which role preset(s) were granted, for display.
/// Authentication (who the user is) is upstream; this is authorization.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class Membership
{
    [BsonId]
    public Guid Id { get; set; }

    public Guid UserId { get; set; }
    public Guid ClientId { get; set; }

    /// <summary>The role preset(s) granted (provenance/display). May be empty for a custom capability grant.</summary>
    [BsonRepresentation(BsonType.String)]
    public IReadOnlyList<LedgerRole> GrantedRoles { get; set; } = [];

    /// <summary>The authoritative resolved capability set (see <see cref="Capabilities"/>).</summary>
    public IReadOnlyList<string> Capabilities { get; set; } = [];

    /// <summary>Pre-migration single-role docs stored their role in "Role"; read-time backfill uses this.</summary>
    [BsonElement("Role")]
    [BsonRepresentation(BsonType.String)]
    [BsonIgnoreIfNull]
    public LedgerRole? LegacyRole { get; set; }
}
