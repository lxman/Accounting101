using MongoDB.Bson;
using MongoDB.Driver;
using MongoDbGenericRepository;

namespace Accounting101.Data;

public class ApplicationDbContext() : IMongoDbContext
{
    public IMongoCollection<TDocument> GetCollection<TDocument>(string partitionKey = null)
    {
        throw new NotImplementedException();
    }

    public void DropCollection<TDocument>(string partitionKey = null)
    {
        throw new NotImplementedException();
    }

    public void SetGuidRepresentation(GuidRepresentation guidRepresentation)
    {
        throw new NotImplementedException();
    }

    public IMongoClient Client { get; }
    public IMongoDatabase Database { get; }
}
