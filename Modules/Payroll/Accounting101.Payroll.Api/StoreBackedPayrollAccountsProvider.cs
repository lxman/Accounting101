using Accounting101.Ledger.Api.Control;

namespace Accounting101.Payroll.Api;

/// <summary>Resolves the five payroll posting accounts per client: the account configured on the
/// posting-accounts admin screen if set, else the process config value (<c>Payroll:Accounts:*</c>) —
/// so behavior is unchanged until a per-client account is chosen.</summary>
public sealed class StoreBackedPayrollAccountsProvider(IPostingAccountsSource source, IConfiguration configuration)
    : IPayrollAccountsProvider
{
    public async Task<PayrollPostingAccounts> GetAccountsAsync(Guid clientId, CancellationToken ct = default)
    {
        IReadOnlyDictionary<string, Guid> stored = await source.GetAsync(clientId, "payroll", ct);
        return new PayrollPostingAccounts
        {
            SalariesExpenseAccountId     = Resolve(stored, "SalariesExpense"),
            PayrollTaxExpenseAccountId   = Resolve(stored, "PayrollTaxExpense"),
            CashAccountId                = Resolve(stored, "Cash"),
            WithholdingsPayableAccountId = Resolve(stored, "WithholdingsPayable"),
            PayrollTaxesPayableAccountId = Resolve(stored, "PayrollTaxesPayable"),
        };
    }

    private Guid Resolve(IReadOnlyDictionary<string, Guid> stored, string slot) =>
        stored.TryGetValue(slot, out Guid id) && id != Guid.Empty
            ? id
            : Guid.TryParse(configuration[$"Payroll:Accounts:{slot}"], out Guid cfg)
                ? cfg
                : throw new InvalidOperationException($"Payroll posting account 'Payroll:Accounts:{slot}' is not configured.");
}
