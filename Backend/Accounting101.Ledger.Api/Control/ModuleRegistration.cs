using MongoDB.Bson.Serialization.Attributes;

namespace Accounting101.Ledger.Api.Control;

/// <summary>
/// A module installed in this deployment, recorded in the control database beside the client
/// registry and memberships. The control-plane record of "which modules exist", and (later) where an
/// out-of-process module's verification credential lives. In-process it lets every authorization
/// confirm the stamped module is registered and enabled.
/// </summary>
public sealed class ModuleRegistration
{
    /// <summary>The module's stable key (also its namespace prefix), e.g. "invoicing".</summary>
    [BsonId]
    public string Key { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>When false, the module is known but barred — every call it makes is denied.</summary>
    public bool Enabled { get; set; }
}
