namespace Accounting101.Ledger.Api.Control;

/// <summary>Read-only per-client posting-account lookup for module providers: the account ids a module
/// posts to for a client, by slot. Empty when the client has configured none (the provider falls back
/// to process config).</summary>
public interface IPostingAccountsSource
{
    Task<IReadOnlyDictionary<string, Guid>> GetAsync(Guid clientId, string moduleKey, CancellationToken ct = default);

    /// <summary>The client's stored category map for a module (category → account id), or null when the
    /// client has none stored. An EMPTY map is a real stored value (the admin cleared the categories)
    /// and must be returned as empty, not null. Default implementation returns null so existing test
    /// fakes and any source without category support need no change.</summary>
    Task<IReadOnlyDictionary<string, Guid>?> GetCategoryMapAsync(Guid clientId, string moduleKey, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, Guid>?>(null);
}

public sealed class StorePostingAccountsSource(PostingAccountStore store) : IPostingAccountsSource
{
    public async Task<IReadOnlyDictionary<string, Guid>> GetAsync(Guid clientId, string moduleKey, CancellationToken ct = default)
    {
        PostingAccountsDoc? doc = await store.GetAsync(clientId, ct);
        return doc is not null && doc.Accounts.TryGetValue(moduleKey, out Dictionary<string, Guid>? slots)
            ? slots
            : new Dictionary<string, Guid>();
    }

    public async Task<IReadOnlyDictionary<string, Guid>?> GetCategoryMapAsync(Guid clientId, string moduleKey, CancellationToken ct = default)
    {
        PostingAccountsDoc? doc = await store.GetAsync(clientId, ct);
        return doc is not null && doc.CategoryMaps.TryGetValue(moduleKey, out Dictionary<string, Guid>? map)
            ? map
            : null;
    }
}
