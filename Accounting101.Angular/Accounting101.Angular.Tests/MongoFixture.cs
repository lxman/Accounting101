using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace Accounting101.Angular.Tests
{
    public class MongoFixture : IDisposable
    {
        public MongoFixture()
        {
            BsonSerializer.RegisterSerializer(new GuidSerializer(GuidRepresentation.Standard));
            ObjectSerializer objectSerializer = new(type => ObjectSerializer.DefaultAllowedTypes(type) || true);
            BsonSerializer.RegisterSerializer(objectSerializer);
        }

        public void Dispose()
        {
        }
    }
}
