using Accounting101.ModuleKit;

namespace Accounting101.Payroll.Api;

/// <summary>Declares the five chart accounts the payroll recipes post to, for readiness checks.
/// Payroll has no dimensioned fold — each account only needs to exist, be Active, and be of the
/// declared type.</summary>
public sealed class PayrollChartRequirements(IPayrollAccountsProvider accounts)
{
    public async Task<IReadOnlyList<AccountRequirement>> ForAsync(Guid clientId, CancellationToken ct = default)
    {
        PayrollPostingAccounts a = await accounts.GetAccountsAsync(clientId, ct);
        return
        [
            new(a.SalariesExpenseAccountId,     "Salaries Expense",      "Expense",   []),
            new(a.PayrollTaxExpenseAccountId,   "Payroll Tax Expense",   "Expense",   []),
            new(a.CashAccountId,                "Cash",                  "Asset",     []),
            new(a.WithholdingsPayableAccountId, "Withholdings Payable",  "Liability", []),
            new(a.PayrollTaxesPayableAccountId, "Payroll Taxes Payable", "Liability", []),
        ];
    }
}
