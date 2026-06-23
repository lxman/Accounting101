using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Accounting101.Ledger.Mongo.Documents;

/// <summary>
/// The engine-managed envelope around a module's opaque document. The engine stores and round-trips
/// <see cref="Body"/> without interpreting it; <see cref="Tags"/> are the module-supplied, indexable
/// fields the engine queries on. Lifecycle/lineage fields carry the evidentiary state machine. Lives in
/// the per-client database, in a module-namespaced collection — so it carries no client id itself.
/// </summary>
public sealed class ModuleDocument
{
    [BsonId]
    public Guid Id { get; set; }

    /// <summary>Module-supplied indexable tags (e.g. Customer, Status, Number). Equality-queryable.</summary>
    public Dictionary<string, string> Tags { get; set; } = new();

    /// <summary>The module's actual document — opaque to the engine.</summary>
    public BsonDocument Body { get; set; } = new();

    [BsonRepresentation(BsonType.String)]
    public DocumentState State { get; set; }

    /// <summary>Gapless per-collection number assigned at Finalize (evidentiary); null otherwise.</summary>
    public long? Sequence { get; set; }

    public Guid? Supersedes { get; set; }
    public Guid? SupersededBy { get; set; }

    public int Version { get; set; }
}

/// <summary>The lifecycle state of a <see cref="ModuleDocument"/>. Which states are valid depends on the
/// collection's policy: plain → Active; reference → Active/Inactive; evidentiary → Draft/Finalized/Superseded/Voided.</summary>
public enum DocumentState
{
    Active,
    Inactive,
    Draft,
    Finalized,
    Superseded,
    Voided,
}
