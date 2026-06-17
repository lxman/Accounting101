using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Tenancy;

/// <summary>
/// Maps a client id to the MongoDB database holding that client's ledger. This is the single
/// tenant-isolation boundary: it resolves only clients registered in the control database, so a
/// request can never reach an unregistered (or another tenant's) database. Where the database
/// physically lives — Atlas, on-prem, a per-client cluster — is hidden behind this seam.
/// </summary>
public interface IClientDatabaseResolver
{
    Task<IMongoDatabase?> ResolveAsync(Guid clientId, CancellationToken cancellationToken = default);
}
