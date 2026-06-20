using MongoDB.Driver;

namespace Accounting101.Invoicing.Mongo;

/// <summary>Invoice persistence — one collection per client database, upsert by id.</summary>
public sealed class MongoInvoiceStore(IInvoicingDatabaseResolver databases) : IInvoiceStore
{
    private const string CollectionName = "invoicing_invoices";

    static MongoInvoiceStore() => InvoicingMongoBootstrap.RegisterOnce();

    public async Task SaveAsync(Guid clientId, Invoice invoice, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        IMongoCollection<InvoiceDocument> collection = await CollectionAsync(clientId, cancellationToken);
        await collection.ReplaceOneAsync(
            i => i.Id == invoice.Id,
            InvoiceDocument.FromDomain(invoice),
            new ReplaceOptions { IsUpsert = true },
            cancellationToken);
    }

    public async Task<Invoice?> GetAsync(Guid clientId, Guid invoiceId, CancellationToken cancellationToken = default)
    {
        IMongoCollection<InvoiceDocument> collection = await CollectionAsync(clientId, cancellationToken);
        InvoiceDocument? doc = await collection.Find(i => i.Id == invoiceId).FirstOrDefaultAsync(cancellationToken);
        return doc?.ToDomain();
    }

    private async Task<IMongoCollection<InvoiceDocument>> CollectionAsync(Guid clientId, CancellationToken cancellationToken) =>
        (await databases.ResolveAsync(clientId, cancellationToken)).GetCollection<InvoiceDocument>(CollectionName);
}
