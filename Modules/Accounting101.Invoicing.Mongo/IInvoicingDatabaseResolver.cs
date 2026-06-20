using MongoDB.Driver;

namespace Accounting101.Invoicing.Mongo;

/// <summary>
/// The module's tenancy seam: resolves a client to its MongoDB. The host wires this to the same
/// resolution the engine uses, so the module's collections land in the client's own database — one
/// client, one database, everything for that client together.
/// </summary>
public interface IInvoicingDatabaseResolver
{
    Task<IMongoDatabase> ResolveAsync(Guid clientId, CancellationToken cancellationToken = default);
}
