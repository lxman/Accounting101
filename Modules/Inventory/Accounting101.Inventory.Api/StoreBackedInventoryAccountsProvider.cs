using Accounting101.Ledger.Api.Control;

namespace Accounting101.Inventory.Api;

/// <summary>Resolves the four inventory posting accounts per client: the account configured on the
/// posting-accounts admin screen if set, else the process config value (<c>Inventory:Accounts:*</c>) —
/// so behavior is unchanged until a per-client account is chosen.</summary>
public sealed class StoreBackedInventoryAccountsProvider(IPostingAccountsSource source, IConfiguration configuration)
    : IInventoryAccountsProvider
{
    public async Task<InventoryPostingAccounts> GetAccountsAsync(Guid clientId, CancellationToken ct = default)
    {
        IReadOnlyDictionary<string, Guid> stored = await source.GetAsync(clientId, "inventory", ct);
        return new InventoryPostingAccounts
        {
            InventoryAssetAccountId      = Resolve(stored, "InventoryAsset"),
            CogsAccountId                = Resolve(stored, "Cogs"),
            GrniClearingAccountId        = Resolve(stored, "GrniClearing"),
            InventoryAdjustmentAccountId = Resolve(stored, "InventoryAdjustment"),
        };
    }

    private Guid Resolve(IReadOnlyDictionary<string, Guid> stored, string slot) =>
        stored.TryGetValue(slot, out Guid id) && id != Guid.Empty
            ? id
            : Guid.TryParse(configuration[$"Inventory:Accounts:{slot}"], out Guid cfg)
                ? cfg
                : throw new InvalidOperationException($"Inventory posting account 'Inventory:Accounts:{slot}' is not configured.");
}
