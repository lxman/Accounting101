using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace Accounting101.Invoicing.Mongo;

/// <summary>
/// One-time Mongo serialization setup for the invoicing module: GUIDs as standard binary, money as
/// Decimal128. Uses TryRegister, so when the module runs in the same process as the engine (which
/// registers the same serializers) whichever runs first wins and the second is a no-op — no conflict.
/// </summary>
public static class InvoicingMongoBootstrap
{
    private static int _registered;

    public static void RegisterOnce()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1)
            return;

        BsonSerializer.TryRegisterSerializer(new GuidSerializer(GuidRepresentation.Standard));
        BsonSerializer.TryRegisterSerializer(new DecimalSerializer(BsonType.Decimal128));
    }
}
