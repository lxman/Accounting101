using Accounting101.Ledger.Api.Control;
using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Tenancy;

/// <summary>
/// Resolves a client's ledger database from the control-DB registry. Returns null for any client
/// not registered in this deployment — the isolation boundary refuses to invent a database for an
/// unknown id. (When a future deployment spreads clients across clusters, this is the one place
/// that changes; the engine above it is unaffected.)
/// </summary>
public sealed class ClientDatabaseResolver(IMongoClient client, ControlStore control) : IClientDatabaseResolver
{
    public async Task<IMongoDatabase?> ResolveAsync(Guid clientId, CancellationToken cancellationToken = default)
    {
        ClientRegistration? registration = await control.GetClientAsync(clientId, cancellationToken);
        return registration is null ? null : client.GetDatabase(registration.DatabaseName);
    }
}
