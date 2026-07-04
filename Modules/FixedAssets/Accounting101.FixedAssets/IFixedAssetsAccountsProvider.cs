namespace Accounting101.FixedAssets;

/// <summary>Supplies the two depreciation posting accounts for a client.</summary>
public interface IFixedAssetsAccountsProvider
{
    Task<FixedAssetsPostingAccounts> GetAccountsAsync(Guid clientId, CancellationToken ct = default);
}
