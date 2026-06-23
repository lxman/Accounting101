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
    private readonly IMongoCollection<ModuleRegistration> _modules;

    static ControlStore() => LedgerMongoBootstrap.RegisterOnce();

    public ControlStore(IMongoDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _clients = database.GetCollection<ClientRegistration>("clients");
        _memberships = database.GetCollection<Membership>("memberships");
        _modules = database.GetCollection<ModuleRegistration>("modules");
    }

    /// <summary>The client's registration, or null if no such client exists in this deployment.</summary>
    public async Task<ClientRegistration?> GetClientAsync(Guid clientId, CancellationToken cancellationToken = default) =>
        await _clients.Find(c => c.Id == clientId).FirstOrDefaultAsync(cancellationToken);

    /// <summary>True iff the user is granted access to the client's books.</summary>
    public async Task<bool> IsMemberAsync(Guid userId, Guid clientId, CancellationToken cancellationToken = default) =>
        await _memberships.Find(m => m.UserId == userId && m.ClientId == clientId).AnyAsync(cancellationToken);

    /// <summary>The user's membership (and role) on the client, or null if they are not a member.</summary>
    public async Task<Membership?> GetMembershipAsync(Guid userId, Guid clientId, CancellationToken cancellationToken = default) =>
        await _memberships.Find(m => m.UserId == userId && m.ClientId == clientId).FirstOrDefaultAsync(cancellationToken);

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

    /// <summary>Grant a user a role on a client's books (idempotent — an existing membership is left as is).</summary>
    public async Task AddMembershipAsync(Guid userId, Guid clientId, LedgerRole role = LedgerRole.Controller, CancellationToken cancellationToken = default)
    {
        if (await IsMemberAsync(userId, clientId, cancellationToken))
            return;

        await _memberships.InsertOneAsync(
            new Membership { Id = Guid.NewGuid(), UserId = userId, ClientId = clientId, Role = role },
            cancellationToken: cancellationToken);
    }

    /// <summary>All clients registered in this deployment.</summary>
    public async Task<IReadOnlyList<ClientRegistration>> ListClientsAsync(CancellationToken cancellationToken = default) =>
        await _clients.Find(FilterDefinition<ClientRegistration>.Empty).ToListAsync(cancellationToken);

    /// <summary>All memberships granted on a client.</summary>
    public async Task<IReadOnlyList<Membership>> GetMembersAsync(Guid clientId, CancellationToken cancellationToken = default) =>
        await _memberships.Find(m => m.ClientId == clientId).ToListAsync(cancellationToken);

    /// <summary>The module's registration, or null if no such module is installed in this deployment.</summary>
    public async Task<ModuleRegistration?> GetModuleAsync(string key, CancellationToken cancellationToken = default) =>
        await _modules.Find(m => m.Key == key).FirstOrDefaultAsync(cancellationToken);

    /// <summary>Register (or update) an installed module — idempotent upsert keyed by the module key.</summary>
    public Task RegisterModuleAsync(ModuleRegistration registration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        return _modules.ReplaceOneAsync(
            m => m.Key == registration.Key,
            registration,
            new ReplaceOptions { IsUpsert = true },
            cancellationToken);
    }

    /// <summary>All modules installed in this deployment.</summary>
    public async Task<IReadOnlyList<ModuleRegistration>> ListModulesAsync(CancellationToken cancellationToken = default) =>
        await _modules.Find(FilterDefinition<ModuleRegistration>.Empty).ToListAsync(cancellationToken);
}
