using Accounting101.Ledger.Api.Control;
using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Tenancy;

/// <summary>
/// Resolves a client's ledger database from a single control-DB registry, against one fixed
/// <see cref="IMongoClient"/>. Returns null for any client not registered — it refuses to invent a
/// database for an unknown id.
/// <para>
/// NOT firm-scoped: the composition root registers <see cref="FirmScopedClientDatabaseResolver"/> for
/// <see cref="IClientDatabaseResolver"/>, which routes through the request firm's control DB and cluster.
/// Do NOT register this type in the host — doing so would bypass firm isolation. It is retained only as a
/// direct, single-tenant resolver for tests that construct a document store without booting the host.
/// </para>
/// </summary>
public sealed class ClientDatabaseResolver(IMongoClient client, ControlStore control) : IClientDatabaseResolver
{
    public async Task<IMongoDatabase?> ResolveAsync(Guid clientId, CancellationToken cancellationToken = default)
    {
        ClientRegistration? registration = await control.GetClientAsync(clientId, cancellationToken);
        return registration is null ? null : client.GetDatabase(registration.DatabaseName);
    }
}
