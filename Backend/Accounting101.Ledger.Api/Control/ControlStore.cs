using Accounting101.Ledger.Mongo.Serialization;
using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Control;

/// <summary>
/// Persistence for the per-deployment control database: the client registry (client id → ledger
/// database) and user→client memberships (the firm's authorization grouping). One control DB per
/// deployment; there is no firm dimension because a deployment serves exactly one firm.
/// </summary>
public sealed class ControlStore
{
    private readonly IMongoCollection<ClientRegistration> _clients;
    private readonly IMongoCollection<Membership> _memberships;

    static ControlStore() => LedgerMongoBootstrap.RegisterOnce();

    public ControlStore(IMongoDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _clients = database.GetCollection<ClientRegistration>("clients");
        _memberships = database.GetCollection<Membership>("memberships");
    }

    /// <summary>The client's registration, or null if no such client exists in this deployment.</summary>
    public async Task<ClientRegistration?> GetClientAsync(Guid clientId, CancellationToken cancellationToken = default) =>
        await _clients.Find(c => c.Id == clientId).FirstOrDefaultAsync(cancellationToken);

    /// <summary>True iff the user is granted access to the client's books.</summary>
    public async Task<bool> IsMemberAsync(Guid userId, Guid clientId, CancellationToken cancellationToken = default) =>
        await _memberships.Find(m => m.UserId == userId && m.ClientId == clientId).AnyAsync(cancellationToken);

    /// <summary>Register (or update) a client and the database that holds its ledger.</summary>
    public Task RegisterClientAsync(ClientRegistration registration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        return _clients.ReplaceOneAsync(
            c => c.Id == registration.Id,
            registration,
            new ReplaceOptions { IsUpsert = true },
            cancellationToken);
    }

    /// <summary>Grant a user access to a client's books (idempotent).</summary>
    public async Task AddMembershipAsync(Guid userId, Guid clientId, CancellationToken cancellationToken = default)
    {
        if (await IsMemberAsync(userId, clientId, cancellationToken))
            return;

        await _memberships.InsertOneAsync(
            new Membership { Id = Guid.NewGuid(), UserId = userId, ClientId = clientId },
            cancellationToken: cancellationToken);
    }
}
