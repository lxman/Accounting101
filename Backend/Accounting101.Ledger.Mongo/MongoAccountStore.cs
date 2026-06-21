using Accounting101.Ledger.Core.Accounts;
using Accounting101.Ledger.Mongo.Documents;
using Accounting101.Ledger.Mongo.Serialization;
using MongoDB.Driver;

namespace Accounting101.Ledger.Mongo;

/// <summary>
/// Per-client chart-of-accounts persistence. Reference data (no balances). Reads return the
/// validated <see cref="ChartOfAccounts"/> aggregate, so a loaded chart is structurally sound
/// or the load throws.
/// </summary>
public sealed class MongoAccountStore
{
    private readonly IMongoCollection<AccountDocument> _accounts;

    static MongoAccountStore() => LedgerMongoBootstrap.RegisterOnce();

    public MongoAccountStore(IMongoDatabase database, string collectionName = "accounts")
    {
        ArgumentNullException.ThrowIfNull(database);
        _accounts = database.GetCollection<AccountDocument>(collectionName);
    }

    /// <summary>
    /// Add or update an account (by its stable id). Joins the given transaction when one is supplied, so
    /// the change can commit atomically with its audit record (chart changes are audited via <c>ChartService</c>).
    /// </summary>
    public Task UpsertAsync(Account account, IClientSessionHandle? session = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        AccountDocument doc = AccountDocument.FromDomain(account);
        FilterDefinition<AccountDocument> filter = Builders<AccountDocument>.Filter.Where(a => a.Id == account.Id);
        ReplaceOptions options = new() { IsUpsert = true };
        return session is null
            ? _accounts.ReplaceOneAsync(filter, doc, options, cancellationToken)
            : _accounts.ReplaceOneAsync(session, filter, doc, options, cancellationToken);
    }

    public async Task<Account?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        AccountDocument? doc = await _accounts.Find(a => a.Id == id).FirstOrDefaultAsync(cancellationToken);
        return doc?.ToDomain();
    }

    /// <summary>Load the client's full chart as a validated aggregate.</summary>
    public async Task<ChartOfAccounts> GetChartAsync(Guid clientId, CancellationToken cancellationToken = default)
    {
        List<AccountDocument> docs = await _accounts.Find(a => a.ClientId == clientId).ToListAsync(cancellationToken);
        return new ChartOfAccounts(docs.Select(d => d.ToDomain()));
    }
}
