using Accounting101.ModuleKit;

namespace Accounting101.Inventory.Api;

/// <summary>Declares the chart accounts the inventory recipes post to and fold, for readiness checks.
/// The Inventory Asset account must require the "Item" dimension its per-item value fold reads.</summary>
public sealed class InventoryChartRequirements(IInventoryAccountsProvider accounts)
{
    public async Task<IReadOnlyList<AccountRequirement>> ForAsync(Guid clientId, CancellationToken ct = default)
    {
        InventoryPostingAccounts a = await accounts.GetAccountsAsync(clientId, ct);
        return
        [
            new(a.InventoryAssetAccountId,       "Inventory Asset",     "Asset",     ["Item"]),
            new(a.CogsAccountId,                 "Cost of Goods Sold",  "Expense",   []),
            new(a.GrniClearingAccountId,         "GRNI Clearing",       "Liability", []),
            new(a.InventoryAdjustmentAccountId,  "Inventory Adjustment","Expense",   []),
        ];
    }
}
