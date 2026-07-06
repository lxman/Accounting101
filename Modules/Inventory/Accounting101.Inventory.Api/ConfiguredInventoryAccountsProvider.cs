using Accounting101.Inventory;

namespace Accounting101.Inventory.Api;

/// <summary>Supplies the four inventory posting accounts from configuration
/// (<c>Inventory:Accounts:InventoryAsset|Cogs|GrniClearing|InventoryAdjustment</c>).
/// No hardcoded numbers.</summary>
public sealed class ConfiguredInventoryAccountsProvider(IConfiguration configuration) : IInventoryAccountsProvider
{
    public Task<InventoryPostingAccounts> GetAccountsAsync(Guid clientId, CancellationToken ct = default) =>
        Task.FromResult(new InventoryPostingAccounts
        {
            InventoryAssetAccountId = Read("Inventory:Accounts:InventoryAsset"),
            CogsAccountId = Read("Inventory:Accounts:Cogs"),
            GrniClearingAccountId = Read("Inventory:Accounts:GrniClearing"),
            InventoryAdjustmentAccountId = Read("Inventory:Accounts:InventoryAdjustment"),
        });

    private Guid Read(string key) =>
        Guid.TryParse(configuration[key], out Guid id)
            ? id
            : throw new InvalidOperationException($"Inventory posting account '{key}' is not configured.");
}
