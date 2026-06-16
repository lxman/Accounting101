using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace Accounting101.Ledger.Mongo;

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
    }
}
