namespace Accounting101.Banking.Cash.Api;

/// <summary>Supplies the cash posting account from configuration
/// (<c>Cash:Accounts:Cash</c>). A single configured set for now; no hardcoded account numbers.</summary>
public sealed class ConfiguredCashAccountsProvider(IConfiguration configuration) : ICashAccountsProvider
{
    public Task<CashPostingAccounts> GetAccountsAsync(Guid clientId, CancellationToken ct = default) =>
        Task.FromResult(new CashPostingAccounts
        {
            CashAccountId = Read("Cash:Accounts:Cash"),
        });

    private Guid Read(string key) =>
        Guid.TryParse(configuration[key], out Guid id)
            ? id
            : throw new InvalidOperationException($"Cash posting account '{key}' is not configured.");
}
