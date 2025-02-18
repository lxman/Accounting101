using MongoDB.Bson;
using MongoDB.Driver;
using MongoDbGenericRepository;

namespace Accounting101.Data;

public class ApplicationDbContext(IMongoClient client, IMongoDatabase db) : IMongoDbContext
{
    public IMongoClient Client { get; } = client;

    public IMongoDatabase Database { get; } = db;

    public IMongoCollection<TDocument> GetCollection<TDocument>(string? partitionKey = null)
    {
        throw new NotImplementedException();
    }

    public void DropCollection<TDocument>(string? partitionKey = null)
    {
        throw new NotImplementedException();
    }

    public void SetGuidRepresentation(GuidRepresentation guidRepresentation)
    {
        throw new NotImplementedException();
    }
}
