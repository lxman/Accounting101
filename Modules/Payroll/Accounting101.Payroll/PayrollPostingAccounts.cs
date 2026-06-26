namespace Accounting101.Payroll;

/// <summary>The five chart accounts the payroll recipes post to. Supplied by configuration;
/// no hardcoded account numbers.</summary>
public sealed record PayrollPostingAccounts
{
    /// <summary>Salaries Expense — debited for gross pay on every payroll run.</summary>
    public required Guid SalariesExpenseAccountId { get; init; }

    /// <summary>Payroll Tax Expense — debited for the employer's share of FICA on every payroll run.</summary>
    public required Guid PayrollTaxExpenseAccountId { get; init; }

    /// <summary>Cash — credited for net pay on a run; credited for total remittance on a tax payment.</summary>
    public required Guid CashAccountId { get; init; }

    /// <summary>Withholdings Payable — credited for income-tax withholdings plus deductions on a run;
    /// debited when the clerk remits those amounts.</summary>
    public required Guid WithholdingsPayableAccountId { get; init; }

    /// <summary>Payroll Taxes Payable — credited for both shares of FICA on a run;
    /// debited when the clerk remits those taxes.</summary>
    public required Guid PayrollTaxesPayableAccountId { get; init; }
}
