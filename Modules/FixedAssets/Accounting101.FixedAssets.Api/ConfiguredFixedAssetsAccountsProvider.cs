using Accounting101.FixedAssets;

namespace Accounting101.FixedAssets.Api;

/// <summary>Supplies the six fixed-assets posting accounts from configuration
/// (<c>FixedAssets:Accounts:DepreciationExpense|AccumulatedDepreciation|AssetCost|DisposalProceeds|GainOnDisposal|LossOnDisposal</c>).
/// No hardcoded numbers.</summary>
public sealed class ConfiguredFixedAssetsAccountsProvider(IConfiguration configuration) : IFixedAssetsAccountsProvider
{
    public Task<FixedAssetsPostingAccounts> GetAccountsAsync(Guid clientId, CancellationToken ct = default) =>
        Task.FromResult(new FixedAssetsPostingAccounts
        {
            DepreciationExpenseAccountId = Read("FixedAssets:Accounts:DepreciationExpense"),
            AccumulatedDepreciationAccountId = Read("FixedAssets:Accounts:AccumulatedDepreciation"),
            AssetCostAccountId = Read("FixedAssets:Accounts:AssetCost"),
            DisposalProceedsAccountId = Read("FixedAssets:Accounts:DisposalProceeds"),
            GainOnDisposalAccountId = Read("FixedAssets:Accounts:GainOnDisposal"),
            LossOnDisposalAccountId = Read("FixedAssets:Accounts:LossOnDisposal"),
        });

    private Guid Read(string key) =>
        Guid.TryParse(configuration[key], out Guid id)
            ? id
            : throw new InvalidOperationException($"Fixed-assets posting account '{key}' is not configured.");
}
