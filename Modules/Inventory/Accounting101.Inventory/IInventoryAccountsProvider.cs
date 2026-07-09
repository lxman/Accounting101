namespace Accounting101.Inventory;

/// <summary>Supplies the inventory posting accounts for a client.</summary>
public interface IInventoryAccountsProvider
{
    Task<InventoryPostingAccounts> GetAccountsAsync(Guid clientId, CancellationToken ct = default);
}
