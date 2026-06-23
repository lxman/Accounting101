namespace Accounting101.Ledger.Api.Auth;

/// <summary>
/// Identifies a calling module. <see cref="Key"/> is the module's stable id and doubles as its imposed
/// storage namespace prefix (collections are <c>{Key}_*</c>). Because the key becomes a Mongo collection
/// prefix, it is validated as collection-name-safe at construction. Authorization is always decided
/// against this value, never against the transport that produced it — the module-side twin of
/// <see cref="Accounting101.Ledger.Mongo.Actor"/>.
/// </summary>
public sealed record ModuleIdentity
{
    private static readonly char[] Unsafe = ['$', '.', ' ', '\0'];

    public ModuleIdentity(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Module key must be non-empty.", nameof(key));
        if (key.IndexOfAny(Unsafe) >= 0)
            throw new ArgumentException($"Module key '{key}' contains a character invalid for a collection prefix.", nameof(key));
        Key = key;
    }

    public string Key { get; }

    /// <summary>The collection-name prefix this module owns, derived from the key (never passed in).</summary>
    public string Prefix => Key + "_";
}
