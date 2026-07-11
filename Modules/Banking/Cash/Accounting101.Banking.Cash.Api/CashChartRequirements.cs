using Accounting101.ModuleKit;

namespace Accounting101.Banking.Cash.Api;

/// <summary>Declares the chart account the cash recipes post to, for readiness checks. Cash has a
/// single account and no dimensioned fold — it only needs to exist, be Active, and be an Asset.</summary>
public sealed class CashChartRequirements(ICashAccountsProvider accounts)
{
    public async Task<IReadOnlyList<AccountRequirement>> ForAsync(Guid clientId, CancellationToken ct = default)
    {
        CashPostingAccounts a = await accounts.GetAccountsAsync(clientId, ct);
        return [ new(a.CashAccountId, "Cash", "Asset", []) ];
    }
}
