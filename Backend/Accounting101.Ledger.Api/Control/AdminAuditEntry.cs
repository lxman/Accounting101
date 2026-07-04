using MongoDB.Bson.Serialization.Attributes;

namespace Accounting101.Ledger.Api.Control;

/// <summary>An append-only record of one control-plane access change (who, what, target, before→after).</summary>
[BsonIgnoreExtraElements]
public sealed class AdminAuditEntry
{
    [BsonId] public Guid Id { get; set; }
    public DateTime Timestamp { get; set; }
    public Guid ActorUserId { get; set; }
    public bool ActorIsDeploymentAdmin { get; set; }
    public string Action { get; set; } = string.Empty;
    public Guid? ClientId { get; set; }
    public Guid? TargetUserId { get; set; }
    public Guid? TargetSetId { get; set; }
    public AuditState? Before { get; set; }
    public AuditState? After { get; set; }
}

/// <summary>A small snapshot of the changed thing: a member's sets/caps, or a set's definition.</summary>
[BsonIgnoreExtraElements]
public sealed class AuditState
{
    public IReadOnlyList<Guid>? SetIds { get; set; }
    public IReadOnlyList<string>? Capabilities { get; set; }
    public string? Name { get; set; }
    public bool? Restricted { get; set; }
}
