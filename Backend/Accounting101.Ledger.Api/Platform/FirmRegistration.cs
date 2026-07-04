using MongoDB.Bson.Serialization.Attributes;

namespace Accounting101.Ledger.Api.Platform;

/// <summary>Billing/access lifecycle of a firm. <see cref="Active"/> is 0 so legacy documents default to it.</summary>
public enum FirmStatus
{
    Active = 0,
    Suspended = 1,
}

/// <summary>
/// One firm (an accounting practice) registered in the platform control database. Maps the firm id to
/// the firm's own control database and to the cluster that holds all of the firm's databases. A firm is
/// the unit of cluster placement — its control DB and every client DB share its <see cref="ClusterKey"/>,
/// which is what makes a firm a self-contained, relocatable set of databases.
/// </summary>
public sealed class FirmRegistration
{
    [BsonId]
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>The MongoDB database name holding this firm's control data (client registry, memberships, …).</summary>
    public string ControlDatabase { get; set; } = string.Empty;

    /// <summary>The cluster this firm lives on. Defaults to the home cluster; a missing field on a legacy
    /// document also deserializes to "default".</summary>
    public string ClusterKey { get; set; } = "default";

    public FirmStatus Status { get; set; } = FirmStatus.Active;

    /// <summary>When the firm was provisioned (UTC).</summary>
    public DateTime CreatedUtc { get; set; }
}
