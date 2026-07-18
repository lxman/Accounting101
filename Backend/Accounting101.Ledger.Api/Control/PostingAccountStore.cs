using Accounting101.Ledger.Mongo.Serialization;
using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Control;

/// <summary>Per-client posting-account configuration: which chart account each module posts to for a
/// given slot. Lives in the control DB (admin config, like the client registration). The map is
/// module key → slot key → account id.</summary>
public sealed class PostingAccountsDoc
{
    public Guid ClientId { get; set; }
    public Dictionary<string, Dictionary<string, Guid>> Accounts { get; set; } = new();

    /// <summary>Per-module dynamic category maps ({moduleKey → {category → account id}}). Parallel to
    /// <see cref="Accounts"/>; only receivables uses it today (invoice revenue-by-category). A stored
    /// entry — even an empty one — is the complete per-client truth and wins over process config.</summary>
    public Dictionary<string, Dictionary<string, Guid>> CategoryMaps { get; set; } = new();
}

public sealed class PostingAccountStore
{
    private readonly IMongoCollection<PostingAccountsDoc> _accounts;

    static PostingAccountStore() => LedgerMongoBootstrap.RegisterOnce();

    public PostingAccountStore(IMongoDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _accounts = database.GetCollection<PostingAccountsDoc>("posting_accounts");
    }

    public async Task<PostingAccountsDoc?> GetAsync(Guid clientId, CancellationToken cancellationToken = default) =>
        await _accounts.Find(d => d.ClientId == clientId).FirstOrDefaultAsync(cancellationToken);

    /// <summary>Upsert the client's posting accounts, replacing the given module's slot map (other
    /// modules untouched).</summary>
    public async Task SetModuleAsync(
        Guid clientId, string moduleKey, IReadOnlyDictionary<string, Guid> slots, CancellationToken cancellationToken = default)
    {
        // Targeted per-module update: writes only the Accounts.<moduleKey> sub-document, so concurrent
        // writes for different modules on the same client cannot clobber each other. Upsert seeds ClientId
        // from the filter on insert.
        await _accounts.UpdateOneAsync(
            d => d.ClientId == clientId,
            Builders<PostingAccountsDoc>.Update.Set($"Accounts.{moduleKey}", new Dictionary<string, Guid>(slots)),
            new UpdateOptions { IsUpsert = true },
            cancellationToken);
    }

    /// <summary>Upsert the client's category map for one module, replacing it wholesale (other modules
    /// and the slot accounts untouched).</summary>
    public async Task SetCategoryMapAsync(
        Guid clientId, string moduleKey, IReadOnlyDictionary<string, Guid> map, CancellationToken cancellationToken = default)
    {
        // Same targeted-update shape as SetModuleAsync: writes only CategoryMaps.<moduleKey>, so
        // concurrent writes to slots or other modules' maps cannot clobber it. Upsert seeds ClientId
        // from the filter on insert.
        await _accounts.UpdateOneAsync(
            d => d.ClientId == clientId,
            Builders<PostingAccountsDoc>.Update.Set($"CategoryMaps.{moduleKey}", new Dictionary<string, Guid>(map)),
            new UpdateOptions { IsUpsert = true },
            cancellationToken);
    }
}
