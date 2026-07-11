using Accounting101.ModuleKit;

namespace Accounting101.FixedAssets.Api;

/// <summary>Declares the chart accounts the fixed-assets recipes post to and fold, for readiness checks.
/// The Accumulated Depreciation account must require the "Asset" dimension its per-asset fold reads.</summary>
public sealed class FixedAssetsChartRequirements(IFixedAssetsAccountsProvider accounts)
{
    public async Task<IReadOnlyList<AccountRequirement>> ForAsync(Guid clientId, CancellationToken ct = default)
    {
        FixedAssetsPostingAccounts a = await accounts.GetAccountsAsync(clientId, ct);
        return
        [
            new(a.AccumulatedDepreciationAccountId, "Accumulated Depreciation", "Asset", ["Asset"]),
            new(a.DepreciationExpenseAccountId,     "Depreciation Expense",     "Expense", []),
            new(a.AssetCostAccountId,               "Fixed Assets (asset cost)", "Asset",  []),
            new(a.DisposalProceedsAccountId,        "Disposal Proceeds",        "Asset",   []),
            new(a.GainOnDisposalAccountId,          "Gain on Disposal",         "Revenue", []),
            new(a.LossOnDisposalAccountId,          "Loss on Disposal",         "Expense", []),
        ];
    }
}
