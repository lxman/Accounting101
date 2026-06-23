namespace Accounting101.Ledger.Api.Documents;

/// <summary>The audit posture of a module collection. Declared once at wiring time; the source of truth
/// for which operations are legal and how much auditing they carry.</summary>
public enum CollectionPolicy
{
    /// <summary>Working data: CRUD, no audit, hard delete allowed.</summary>
    Plain,

    /// <summary>Master/reference data: mutable but every change audited; soft Deactivate, no hard delete.</summary>
    Reference,

    /// <summary>Evidentiary data: draft → finalize (append-only) → supersede/void; audited on the chain.</summary>
    Evidentiary,
}

/// <summary>A module's declared collections, each with a policy and its indexed tag keys.</summary>
public sealed class ModuleManifest
{
    private readonly IReadOnlyDictionary<string, CollectionPolicy> _policies;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _indexedTags;

    public ModuleManifest(
        IReadOnlyDictionary<string, CollectionPolicy> policies,
        IReadOnlyDictionary<string, IReadOnlyList<string>> indexedTags)
    {
        _policies = policies;
        _indexedTags = indexedTags;
    }

    public CollectionPolicy PolicyOf(string collection) =>
        _policies.TryGetValue(collection, out CollectionPolicy policy)
            ? policy
            : throw new ModuleDocumentException($"Collection '{collection}' is not declared in the module manifest.");

    public IReadOnlyList<string> IndexedTags(string collection) =>
        _indexedTags.TryGetValue(collection, out IReadOnlyList<string>? tags) ? tags : [];

    public IReadOnlyCollection<string> Collections => (IReadOnlyCollection<string>)_policies.Keys;
}

/// <summary>Fluent builder a module uses to declare its collections at <c>AddModule</c> time.</summary>
public sealed class ModuleManifestBuilder
{
    private readonly Dictionary<string, CollectionPolicy> _policies = new();
    private readonly Dictionary<string, IReadOnlyList<string>> _indexedTags = new();

    public ModuleManifestBuilder Plain(string collection) => Add(collection, CollectionPolicy.Plain, []);
    public ModuleManifestBuilder Reference(string collection, params string[] indexedTags) => Add(collection, CollectionPolicy.Reference, indexedTags);
    public ModuleManifestBuilder Evidentiary(string collection, params string[] indexedTags) => Add(collection, CollectionPolicy.Evidentiary, indexedTags);

    private ModuleManifestBuilder Add(string collection, CollectionPolicy policy, IReadOnlyList<string> indexedTags)
    {
        if (string.IsNullOrWhiteSpace(collection))
            throw new ArgumentException("Collection name must be non-empty.", nameof(collection));
        _policies[collection] = policy;
        _indexedTags[collection] = indexedTags;
        return this;
    }

    public ModuleManifest Build() => new(_policies, _indexedTags);
}
