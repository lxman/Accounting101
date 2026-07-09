using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Bson.Serialization.Serializers;

namespace Accounting101.Ledger.Mongo.Serialization;

/// <summary>
/// One-time MongoDB serialization setup for the ledger. Stores GUIDs as the
/// standard binary subtype (4). Amounts (Decimal128) and enums (as strings) are
/// configured per-property on the document DTOs. Call once at startup (the store
/// also calls it from its static constructor).
/// </summary>
public static class LedgerMongoBootstrap
{
    private static int _registered;

    public static void RegisterOnce()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1)
            return;

        BsonSerializer.TryRegisterSerializer(new GuidSerializer(GuidRepresentation.Standard));

        // Money is Decimal128 everywhere — including projection map values and $inc
        // operands, so balance increments are numeric (not string) operations.
        BsonSerializer.TryRegisterSerializer(new DecimalSerializer(BsonType.Decimal128));

        // Tolerate documents written under an older schema on read: a field removed from a
        // module-document body (e.g. the per-invoice "Allocations" array dropped when that split
        // moved onto ledger dimensions) must not fail deserialization of pre-existing documents.
        // Body records are serialized generically by ScopedDocumentStore, so they cannot carry a
        // [BsonIgnoreExtraElements] attribute (the domain has no MongoDB dependency) — the convention
        // applies it globally instead. Removed fields are ignored on read; the value now lives
        // elsewhere (folded from the ledger), never in the stale element.
        ConventionRegistry.Register(
            "IgnoreExtraElements",
            new ConventionPack { new IgnoreExtraElementsConvention(true) },
            _ => true);
    }
}
