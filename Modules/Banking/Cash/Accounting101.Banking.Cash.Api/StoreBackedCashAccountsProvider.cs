using Accounting101.Ledger.Api.Control;

namespace Accounting101.Banking.Cash.Api;

/// <summary>Resolves the cash posting account per client: the account configured on the posting-accounts
/// admin screen if set, else the process config value (<c>Cash:Accounts:Cash</c>) — so behavior is
/// unchanged until a per-client account is chosen.</summary>
public sealed class StoreBackedCashAccountsProvider(IPostingAccountsSource source, IConfiguration configuration)
    : ICashAccountsProvider
{
    public async Task<CashPostingAccounts> GetAccountsAsync(Guid clientId, CancellationToken ct = default)
    {
        IReadOnlyDictionary<string, Guid> stored = await source.GetAsync(clientId, "cash", ct);
        Guid cash = stored.TryGetValue("Cash", out Guid id) && id != Guid.Empty
            ? id
            : ConfiguredFallback("Cash:Accounts:Cash");
        return new CashPostingAccounts { CashAccountId = cash };
    }

    private Guid ConfiguredFallback(string key) =>
        Guid.TryParse(configuration[key], out Guid id)
            ? id
            : throw new InvalidOperationException($"Cash posting account '{key}' is not configured.");
}
