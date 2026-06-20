using System.Collections.Concurrent;
using Accounting101.Ledger.Mongo;
using Accounting101.Ledger.Mongo.Reporting;
using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Tenancy;

/// <summary>
/// Builds the <see cref="ClientLedger"/> for a resolved client database. The stores are cheap,
/// stateless wrappers over collections, so they are constructed per request; indexes are ensured
/// once per client per process. Returns null if the client is not registered (the resolver
/// refused it) — the caller maps that to the appropriate HTTP result.
/// </summary>
public sealed class ClientLedgerFactory(IClientDatabaseResolver resolver)
{
    private readonly ConcurrentDictionary<Guid, bool> _indexed = new();

    public async Task<ClientLedger?> CreateAsync(Guid clientId, CancellationToken cancellationToken = default)
    {
        IMongoDatabase? database = await resolver.ResolveAsync(clientId, cancellationToken);
        if (database is null)
            return null;

        MongoJournalStore journal = new(database);
        MongoBalanceProjection projection = new(database, journal);
        MongoCheckpointStore checkpoints = new(database);
        MongoAuditLog audit = new(database);
        MongoAccountStore accounts = new(database);
        LedgerService service = new(database.Client, journal, projection, checkpoints, audit);
        FinancialStatementService statements = new(journal, accounts);

        if (_indexed.TryAdd(clientId, true))
        {
            await journal.EnsureIndexesAsync(cancellationToken);
            await audit.EnsureIndexesAsync(cancellationToken);
        }

        return new ClientLedger(service, journal, audit, projection, checkpoints, accounts, statements);
    }
}
