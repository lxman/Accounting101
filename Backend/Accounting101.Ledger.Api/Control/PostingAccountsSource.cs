namespace Accounting101.Ledger.Api.Control;

/// <summary>Read-only per-client posting-account lookup for module providers: the account ids a module
/// posts to for a client, by slot. Empty when the client has configured none (the provider falls back
/// to process config).</summary>
public interface IPostingAccountsSource
{
    Task<IReadOnlyDictionary<string, Guid>> GetAsync(Guid clientId, string moduleKey, CancellationToken ct = default);
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
}
