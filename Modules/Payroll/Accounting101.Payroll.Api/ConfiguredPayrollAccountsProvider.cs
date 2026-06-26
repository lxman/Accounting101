namespace Accounting101.Payroll.Api;

/// <summary>Supplies the five payroll posting accounts from configuration
/// (<c>Payroll:Accounts:SalariesExpense|PayrollTaxExpense|Cash|WithholdingsPayable|PayrollTaxesPayable</c>).
/// A single configured set for now; no hardcoded account numbers.</summary>
public sealed class ConfiguredPayrollAccountsProvider(IConfiguration configuration) : IPayrollAccountsProvider
{
    public Task<PayrollPostingAccounts> GetAccountsAsync(Guid clientId, CancellationToken ct = default) =>
        Task.FromResult(new PayrollPostingAccounts
        {
            SalariesExpenseAccountId = Read("Payroll:Accounts:SalariesExpense"),
            PayrollTaxExpenseAccountId = Read("Payroll:Accounts:PayrollTaxExpense"),
            CashAccountId = Read("Payroll:Accounts:Cash"),
            WithholdingsPayableAccountId = Read("Payroll:Accounts:WithholdingsPayable"),
            PayrollTaxesPayableAccountId = Read("Payroll:Accounts:PayrollTaxesPayable"),
        });

    private Guid Read(string key) =>
        Guid.TryParse(configuration[key], out Guid id)
            ? id
            : throw new InvalidOperationException($"Payroll posting account '{key}' is not configured.");
}
