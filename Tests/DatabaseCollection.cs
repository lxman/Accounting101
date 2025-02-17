namespace Tests
{
    [CollectionDefinition("Mongo Database")]
    public class DatabaseCollection : ICollectionFixture<MongoFixture>
    {
    }
}
