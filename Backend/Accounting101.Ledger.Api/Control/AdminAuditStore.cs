using Accounting101.Ledger.Mongo.Serialization;
using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Control;

/// <summary>Append-only audit of control-plane access changes. Exposes ONLY append + query — there is
/// deliberately no update or delete method, so the application cannot rewrite the record.</summary>
public sealed class AdminAuditStore
{
    private readonly IMongoCollection<AdminAuditEntry> _entries;

    static AdminAuditStore() => LedgerMongoBootstrap.RegisterOnce();

    public AdminAuditStore(IMongoDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _entries = database.GetCollection<AdminAuditEntry>("adminAudit");
    }

    /// <summary>Append one entry. Insert-only.</summary>
    public Task AppendAsync(AdminAuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return _entries.InsertOneAsync(entry, cancellationToken: cancellationToken);
    }

    /// <summary>Entries matching the filter, newest-first, capped at <see cref="AdminAuditFilter.Limit"/>.</summary>
    public async Task<IReadOnlyList<AdminAuditEntry>> QueryAsync(AdminAuditFilter filter, CancellationToken cancellationToken = default)
    {
        FilterDefinitionBuilder<AdminAuditEntry> b = Builders<AdminAuditEntry>.Filter;
        List<FilterDefinition<AdminAuditEntry>> clauses = [];
        if (filter.ClientId is { } clientId) clauses.Add(b.Eq(x => x.ClientId, clientId));
        if (filter.ActorUserId is { } actor) clauses.Add(b.Eq(x => x.ActorUserId, actor));
        if (filter.TargetUserId is { } target) clauses.Add(b.Eq(x => x.TargetUserId, target));
        FilterDefinition<AdminAuditEntry> query = clauses.Count > 0 ? b.And(clauses) : b.Empty;

        return await _entries.Find(query)
            .SortByDescending(x => x.Timestamp)
            .Limit(filter.Limit)
            .ToListAsync(cancellationToken);
    }
}

/// <summary>Filter for <see cref="AdminAuditStore.QueryAsync"/>. All criteria optional; ANDed.</summary>
public sealed record AdminAuditFilter(Guid? ClientId = null, Guid? ActorUserId = null, Guid? TargetUserId = null, int Limit = 100);
