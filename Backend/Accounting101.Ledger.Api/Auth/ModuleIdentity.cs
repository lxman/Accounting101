namespace Accounting101.Ledger.Api.Auth;

/// <summary>
/// Identifies a calling module. <see cref="Key"/> is the module's stable id and doubles as its
/// imposed storage namespace prefix (collections are <c>{Key}_*</c>). Authorization is always
/// decided against this value, never against the transport that produced it — the module-side twin
/// of <see cref="Accounting101.Ledger.Mongo.Actor"/>.
/// </summary>
public sealed record ModuleIdentity(string Key)
{
    /// <summary>The collection-name prefix this module owns, derived from the key (never passed in).</summary>
    public string Prefix => Key + "_";
}
