using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Api.Platform;
using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Tenancy;

/// <summary>
/// Resolves a client's ledger database within the current request's firm: the client must be registered
/// in the firm's own control DB (the scoped <see cref="ControlStore"/>), and its ledger lives on the firm's
/// cluster. A clientId not in this firm's registry returns null — which is the structural isolation
/// boundary: one firm can never name another firm's client, because it has no registry entry to resolve
/// it through.
/// </summary>
public sealed class FirmScopedClientDatabaseResolver(
    FirmScope scope, ControlStore control, IMongoClientFactory factory) : IClientDatabaseResolver
{
    public async Task<IMongoDatabase?> ResolveAsync(Guid clientId, CancellationToken cancellationToken = default)
    {
        ClientRegistration? registration = await control.GetClientAsync(clientId, cancellationToken);
        if (registration is null)
            return null;

        IMongoClient client = await factory.GetAsync(scope.RequireFirm().ClusterKey, cancellationToken);
        return client.GetDatabase(registration.DatabaseName);
    }
}
