using Accounting101.FixedAssets;

namespace Accounting101.FixedAssets.Api;

/// <summary>Supplies the two depreciation posting accounts from configuration
/// (<c>FixedAssets:Accounts:DepreciationExpense|AccumulatedDepreciation</c>). No hardcoded numbers.</summary>
public sealed class ConfiguredFixedAssetsAccountsProvider(IConfiguration configuration) : IFixedAssetsAccountsProvider
{
    public Task<FixedAssetsPostingAccounts> GetAccountsAsync(Guid clientId, CancellationToken ct = default) =>
        Task.FromResult(new FixedAssetsPostingAccounts
        {
            DepreciationExpenseAccountId = Read("FixedAssets:Accounts:DepreciationExpense"),
            AccumulatedDepreciationAccountId = Read("FixedAssets:Accounts:AccumulatedDepreciation"),
        });

    private Guid Read(string key) =>
        Guid.TryParse(configuration[key], out Guid id)
            ? id
            : throw new InvalidOperationException($"Fixed-assets posting account '{key}' is not configured.");
}
