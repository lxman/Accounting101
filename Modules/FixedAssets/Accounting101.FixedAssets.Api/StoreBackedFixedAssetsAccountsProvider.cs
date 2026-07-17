using Accounting101.Ledger.Api.Control;

namespace Accounting101.FixedAssets.Api;

/// <summary>Resolves the six fixed-assets posting accounts per client: the account configured on the
/// posting-accounts admin screen if set, else the process config value (<c>FixedAssets:Accounts:*</c>) —
/// so behavior is unchanged until a per-client account is chosen.</summary>
public sealed class StoreBackedFixedAssetsAccountsProvider(IPostingAccountsSource source, IConfiguration configuration)
    : IFixedAssetsAccountsProvider
{
    public async Task<FixedAssetsPostingAccounts> GetAccountsAsync(Guid clientId, CancellationToken ct = default)
    {
        IReadOnlyDictionary<string, Guid> stored = await source.GetAsync(clientId, "fixedassets", ct);
        return new FixedAssetsPostingAccounts
        {
            DepreciationExpenseAccountId     = Resolve(stored, "DepreciationExpense"),
            AccumulatedDepreciationAccountId = Resolve(stored, "AccumulatedDepreciation"),
            AssetCostAccountId               = Resolve(stored, "AssetCost"),
            DisposalProceedsAccountId        = Resolve(stored, "DisposalProceeds"),
            GainOnDisposalAccountId          = Resolve(stored, "GainOnDisposal"),
            LossOnDisposalAccountId          = Resolve(stored, "LossOnDisposal"),
        };
    }

    private Guid Resolve(IReadOnlyDictionary<string, Guid> stored, string slot) =>
        stored.TryGetValue(slot, out Guid id) && id != Guid.Empty
            ? id
            : Guid.TryParse(configuration[$"FixedAssets:Accounts:{slot}"], out Guid cfg)
                ? cfg
                : throw new InvalidOperationException($"Fixed-assets posting account 'FixedAssets:Accounts:{slot}' is not configured.");
}
