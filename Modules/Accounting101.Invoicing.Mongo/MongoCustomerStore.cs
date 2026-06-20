using MongoDB.Driver;

namespace Accounting101.Invoicing.Mongo;

/// <summary>Customer persistence — one collection per client database, upsert by id.</summary>
public sealed class MongoCustomerStore(IInvoicingDatabaseResolver databases) : ICustomerStore
{
    private const string CollectionName = "invoicing_customers";

    static MongoCustomerStore() => InvoicingMongoBootstrap.RegisterOnce();

    public async Task SaveAsync(Guid clientId, Customer customer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(customer);
        IMongoCollection<CustomerDocument> collection = await CollectionAsync(clientId, cancellationToken);
        await collection.ReplaceOneAsync(
            c => c.Id == customer.Id,
            CustomerDocument.FromDomain(customer),
            new ReplaceOptions { IsUpsert = true },
            cancellationToken);
    }

    public async Task<Customer?> GetAsync(Guid clientId, Guid customerId, CancellationToken cancellationToken = default)
    {
        IMongoCollection<CustomerDocument> collection = await CollectionAsync(clientId, cancellationToken);
        CustomerDocument? doc = await collection.Find(c => c.Id == customerId).FirstOrDefaultAsync(cancellationToken);
        return doc?.ToDomain();
    }

    private async Task<IMongoCollection<CustomerDocument>> CollectionAsync(Guid clientId, CancellationToken cancellationToken) =>
        (await databases.ResolveAsync(clientId, cancellationToken)).GetCollection<CustomerDocument>(CollectionName);
}
