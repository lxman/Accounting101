using Accounting101.Ledger.Mongo.Serialization;
using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Control;

/// <summary>
/// Persistence for the per-deployment control database: the client registry (client id → ledger
/// database), user→client memberships (the firm's authorization grouping), and the named
/// capability sets. One control DB per deployment; there is no firm dimension because a
/// deployment serves exactly one firm.
/// </summary>
public sealed class ControlStore
{
    private readonly IMongoCollection<ClientRegistration> _clients;
    private readonly IMongoCollection<Membership> _memberships;
    private readonly IMongoCollection<ModuleRegistration> _modules;
    private readonly IMongoCollection<CapabilitySet> _capabilitySets;

    static ControlStore() => LedgerMongoBootstrap.RegisterOnce();

    public ControlStore(IMongoDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _clients = database.GetCollection<ClientRegistration>("clients");
        _memberships = database.GetCollection<Membership>("memberships");
        _modules = database.GetCollection<ModuleRegistration>("modules");
        _capabilitySets = database.GetCollection<CapabilitySet>("capabilitySets");
    }

    /// <summary>The client's registration, or null if no such client exists in this deployment.</summary>
    public async Task<ClientRegistration?> GetClientAsync(Guid clientId, CancellationToken cancellationToken = default) =>
        await _clients.Find(c => c.Id == clientId).FirstOrDefaultAsync(cancellationToken);

    /// <summary>True iff the user is granted access to the client's books.</summary>
    public async Task<bool> IsMemberAsync(Guid userId, Guid clientId, CancellationToken cancellationToken = default) =>
        await _memberships.Find(m => m.UserId == userId && m.ClientId == clientId).AnyAsync(cancellationToken);

    /// <summary>The user's membership on the client with capabilities resolved from its referenced
    /// sets (live-binding), or null if not a member.</summary>
    public async Task<Membership?> GetMembershipAsync(Guid userId, Guid clientId, CancellationToken cancellationToken = default)
    {
        Membership? m = await _memberships.Find(m => m.UserId == userId && m.ClientId == clientId).FirstOrDefaultAsync(cancellationToken);
        if (m is null) return null;
        IReadOnlyList<CapabilitySet> catalog = await ListCapabilitySetsAsync(cancellationToken);
        return Resolve(m, catalog);
    }

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

    /// <summary>Replace a client's entitled module keys (default-closed access gate + billing meter).
    /// Returns false when no such client exists in this firm's control DB. Idempotent.</summary>
    public async Task<bool> SetClientModulesAsync(Guid clientId, IReadOnlyList<string> moduleKeys, CancellationToken cancellationToken = default)
    {
        UpdateResult result = await _clients.UpdateOneAsync(
            c => c.Id == clientId,
            Builders<ClientRegistration>.Update.Set(c => c.EnabledModules, moduleKeys),
            cancellationToken: cancellationToken);
        return result.MatchedCount > 0;
    }

    /// <summary>Grant a user a role on a client's books (idempotent — an existing membership is left as is).</summary>
    public Task AddMembershipAsync(Guid userId, Guid clientId, LedgerRole role = LedgerRole.Controller, CancellationToken cancellationToken = default) =>
        AddMembershipRolesAsync(userId, clientId, [role], cancellationToken);

    /// <summary>Grant a user one or more role presets; the stored capability set is their union.</summary>
    public async Task AddMembershipRolesAsync(Guid userId, Guid clientId, IReadOnlyList<LedgerRole> roles, CancellationToken cancellationToken = default)
    {
        if (await IsMemberAsync(userId, clientId, cancellationToken))
            return;

        await _memberships.InsertOneAsync(
            new Membership
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ClientId = clientId,
                GrantedRoles = roles,
                Capabilities = [.. RolePresets.CapabilitiesFor(roles)],
            },
            cancellationToken: cancellationToken);
    }

    /// <summary>Create or replace a member's granted roles + capability set (the authoritative grant).</summary>
    public Task SetMembershipAsync(Guid userId, Guid clientId, IReadOnlyList<LedgerRole> roles, IReadOnlyList<string> capabilities, CancellationToken cancellationToken = default)
    {
        var update = Builders<Membership>.Update
            .Set(m => m.GrantedRoles, roles)
            .Set(m => m.Capabilities, capabilities)
            .Set(m => m.GrantedSetIds, Array.Empty<Guid>())
            .SetOnInsert(m => m.Id, Guid.NewGuid())
            .SetOnInsert(m => m.UserId, userId)
            .SetOnInsert(m => m.ClientId, clientId);

        return _memberships.UpdateOneAsync(
            m => m.UserId == userId && m.ClientId == clientId,
            update,
            new UpdateOptions { IsUpsert = true },
            cancellationToken);
    }

    /// <summary>Assign a member to capability sets (the live-bound grant). Upsert: sets
    /// <see cref="Membership.GrantedSetIds"/> and clears the legacy role list + inline capability copy —
    /// capabilities are resolved from the referenced sets at read time.</summary>
    public Task SetMembershipSetsAsync(Guid userId, Guid clientId, IReadOnlyList<Guid> setIds, CancellationToken cancellationToken = default)
    {
        UpdateDefinition<Membership> update = Builders<Membership>.Update
            .Set(m => m.GrantedSetIds, setIds)
            .Set(m => m.GrantedRoles, Array.Empty<LedgerRole>())
            .Set(m => m.Capabilities, Array.Empty<string>())
            .SetOnInsert(m => m.Id, Guid.NewGuid())
            .SetOnInsert(m => m.UserId, userId)
            .SetOnInsert(m => m.ClientId, clientId);

        return _memberships.UpdateOneAsync(
            m => m.UserId == userId && m.ClientId == clientId,
            update,
            new UpdateOptions { IsUpsert = true },
            cancellationToken);
    }

    /// <summary>How many memberships reference the given capability set (deployment-wide) — the
    /// blast radius of editing or deleting it.</summary>
    public Task<long> CountMembersReferencingSetAsync(Guid setId, CancellationToken cancellationToken = default) =>
        _memberships.CountDocumentsAsync(
            Builders<Membership>.Filter.AnyEq(m => m.GrantedSetIds, setId),
            cancellationToken: cancellationToken);

    /// <summary>One-time migration (idempotent): for every membership that still has no set references
    /// but does carry granted roles, set <see cref="Membership.GrantedSetIds"/> to the built-in sets of
    /// the same name. Role-based members created before AC-2 become explicit, live-bound set references.
    /// Members already carrying set ids (or with no roles) are skipped.</summary>
    public async Task BackfillGrantedSetIdsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<CapabilitySet> catalog = await ListCapabilitySetsAsync(cancellationToken);
        Dictionary<string, Guid> idByName = catalog.ToDictionary(s => s.Name, s => s.Id, StringComparer.OrdinalIgnoreCase);

        List<Membership> all = await _memberships.Find(FilterDefinition<Membership>.Empty).ToListAsync(cancellationToken);
        foreach (Membership m in all)
        {
            if (m.GrantedSetIds.Count > 0 || m.GrantedRoles.Count == 0) continue;

            List<Guid> ids = [];
            foreach (LedgerRole role in m.GrantedRoles)
                if (idByName.TryGetValue(role.ToString(), out Guid id))
                    ids.Add(id);
            if (ids.Count == 0) continue;

            await _memberships.UpdateOneAsync(
                x => x.Id == m.Id,
                Builders<Membership>.Update.Set(x => x.GrantedSetIds, ids),
                cancellationToken: cancellationToken);
        }
    }

    /// <summary>Remove a member from a client's books.</summary>
    public Task RemoveMembershipAsync(Guid userId, Guid clientId, CancellationToken cancellationToken = default) =>
        _memberships.DeleteOneAsync(m => m.UserId == userId && m.ClientId == clientId, cancellationToken);

    /// <summary>All clients registered in this deployment.</summary>
    public async Task<IReadOnlyList<ClientRegistration>> ListClientsAsync(CancellationToken cancellationToken = default) =>
        await _clients.Find(FilterDefinition<ClientRegistration>.Empty).ToListAsync(cancellationToken);

    /// <summary>All memberships granted on a client (capabilities resolved from referenced sets).</summary>
    public async Task<IReadOnlyList<Membership>> GetMembersAsync(Guid clientId, CancellationToken cancellationToken = default)
    {
        List<Membership> members = await _memberships.Find(m => m.ClientId == clientId).ToListAsync(cancellationToken);
        IReadOnlyList<CapabilitySet> catalog = await ListCapabilitySetsAsync(cancellationToken);
        return members.Select(m => Resolve(m, catalog)).ToList();
    }

    /// <summary>Resolve a membership's capabilities per the AC-2 precedence: referenced sets (union of
    /// their CURRENT caps) → built-in sets matching granted roles (role grants are live-bound too) →
    /// stored inline caps with the pre-migration Role backfill. Read-only derivation; no write.</summary>
    private static Membership Resolve(Membership m, IReadOnlyList<CapabilitySet> catalog)
    {
        // 1) Explicit set references are the go-forward authority.
        if (m.GrantedSetIds.Count > 0)
        {
            Dictionary<Guid, CapabilitySet> byId = catalog.ToDictionary(s => s.Id);
            HashSet<string> union = [];
            foreach (Guid id in m.GrantedSetIds)
                if (byId.TryGetValue(id, out CapabilitySet? s))
                    union.UnionWith(s.Capabilities);
            m.Capabilities = [.. union];
            return m;
        }

        // 2) Legacy role grant with no set ids yet: live-bind to the built-in sets of the same name so
        //    an owner's edit to a built-in set flows to role-based members too. If the sets are not
        //    seeded (e.g. a direct pre-startup read), fall through to the stored caps.
        if (m.GrantedRoles.Count > 0)
        {
            Dictionary<string, CapabilitySet> byName = catalog.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
            HashSet<string> union = [];
            bool matched = false;
            foreach (LedgerRole role in m.GrantedRoles)
                if (byName.TryGetValue(role.ToString(), out CapabilitySet? s))
                {
                    union.UnionWith(s.Capabilities);
                    matched = true;
                }
            if (matched)
            {
                m.Capabilities = [.. union];
                return m;
            }
        }

        // 3) Pre-migration single-Role doc or a truly custom inline grant: keep the stored caps.
        return HydrateLegacy(m);
    }

    /// <summary>Backfill a pre-migration (Role-only) doc to the capability shape at read time (no write).</summary>
    private static Membership HydrateLegacy(Membership m)
    {
        if (m.Capabilities.Count == 0 && m.LegacyRole is { } role)
        {
            m.GrantedRoles = [role];
            m.Capabilities = [.. RolePresets.For(role)];
        }
        return m;
    }

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

    /// <summary>Idempotently upsert each installed module's registration into this control DB. Used both by
    /// the startup registrar (default firm) and by firm provisioning (a newly created firm's control DB), so
    /// a provisioned firm holds the same module set + process-global secrets as the default firm.</summary>
    public async Task SeedModulesAsync(IEnumerable<ModuleRegistration> modules, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modules);
        foreach (ModuleRegistration module in modules)
            await RegisterModuleAsync(module, cancellationToken);
    }

    /// <summary>All capability sets in this deployment (built-in + custom).</summary>
    public async Task<IReadOnlyList<CapabilitySet>> ListCapabilitySetsAsync(CancellationToken cancellationToken = default) =>
        await _capabilitySets.Find(FilterDefinition<CapabilitySet>.Empty).ToListAsync(cancellationToken);

    /// <summary>The capability set with this id, or null.</summary>
    public async Task<CapabilitySet?> GetCapabilitySetAsync(Guid id, CancellationToken cancellationToken = default) =>
        await _capabilitySets.Find(s => s.Id == id).FirstOrDefaultAsync(cancellationToken);

    /// <summary>The capability set with this name (case-insensitive), or null. Sets are few, so an
    /// in-memory scan is cheaper and simpler than a collated index query.</summary>
    public async Task<CapabilitySet?> GetCapabilitySetByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        List<CapabilitySet> all = await _capabilitySets.Find(FilterDefinition<CapabilitySet>.Empty).ToListAsync(cancellationToken);
        return all.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Insert a new capability set.</summary>
    public Task CreateCapabilitySetAsync(CapabilitySet set, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(set);
        return _capabilitySets.InsertOneAsync(set, cancellationToken: cancellationToken);
    }

    /// <summary>Replace an existing capability set (matched by id).</summary>
    public Task UpdateCapabilitySetAsync(CapabilitySet set, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(set);
        return _capabilitySets.ReplaceOneAsync(s => s.Id == set.Id, set, cancellationToken: cancellationToken);
    }

    /// <summary>Delete a capability set by id.</summary>
    public Task DeleteCapabilitySetAsync(Guid id, CancellationToken cancellationToken = default) =>
        _capabilitySets.DeleteOneAsync(s => s.Id == id, cancellationToken);

    /// <summary>Seed one built-in capability set per <see cref="LedgerRole"/> preset, idempotently.
    /// Persist-in-place: a set whose name already exists is left untouched (an owner's edits survive
    /// restarts); only missing names are inserted. Also ensures a unique index on <c>Name</c>.</summary>
    public async Task SeedBuiltinCapabilitySetsAsync(CancellationToken cancellationToken = default)
    {
        await _capabilitySets.Indexes.CreateOneAsync(
            new CreateIndexModel<CapabilitySet>(
                Builders<CapabilitySet>.IndexKeys.Ascending(s => s.Name),
                new CreateIndexOptions { Unique = true }),
            cancellationToken: cancellationToken);

        foreach (LedgerRole role in Enum.GetValues<LedgerRole>())
        {
            string name = role.ToString();
            CapabilitySet? existing = await GetCapabilitySetByNameAsync(name, cancellationToken);
            if (existing is not null) continue;

            await _capabilitySets.InsertOneAsync(
                new CapabilitySet
                {
                    Id = Guid.NewGuid(),
                    Name = name,
                    Description = $"Built-in preset for the {name} role.",
                    Capabilities = [.. RolePresets.For(role)],
                    Builtin = true,
                    Restricted = role == LedgerRole.Admin,
                },
                cancellationToken: cancellationToken);
        }

        // Narrow admin built-ins — one delegable single-power admin set each, so granting (say) just
        // user management is one click instead of a hand-assembled set that tempts a full-Admin grant.
        (string Name, string[] Capabilities)[] narrowAdmins =
        [
            ("User Admin", [Capabilities.AdminUsers, Capabilities.GlRead]),
            ("Fiscal Admin", [Capabilities.AdminFiscal, Capabilities.GlRead]),
            ("Posting-Accounts Admin", [Capabilities.AdminPostingAccounts, Capabilities.GlRead]),
        ];
        foreach ((string name, string[] capabilities) in narrowAdmins)
        {
            if (await GetCapabilitySetByNameAsync(name, cancellationToken) is not null) continue;
            await _capabilitySets.InsertOneAsync(
                new CapabilitySet
                {
                    Id = Guid.NewGuid(),
                    Name = name,
                    Description = $"Built-in narrow admin set: {name}.",
                    Capabilities = capabilities,
                    Builtin = true,
                    Restricted = false,
                },
                cancellationToken: cancellationToken);
        }

        // Security invariant: the built-in Admin god-set is always deployment-restricted. Reconcile a
        // deployment seeded before the Restricted flag existed (persist-in-place skips it on re-seed) by
        // flipping false→true in place. This is a non-negotiable default, not an owner-editable preference.
        CapabilitySet? adminSet = await GetCapabilitySetByNameAsync(LedgerRole.Admin.ToString(), cancellationToken);
        if (adminSet is { Builtin: true, Restricted: false })
        {
            adminSet.Restricted = true;
            await UpdateCapabilitySetAsync(adminSet, cancellationToken);
        }
    }
}
