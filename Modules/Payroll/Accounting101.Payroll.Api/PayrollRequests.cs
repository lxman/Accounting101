namespace Accounting101.Payroll.Api;

/// <summary>Record a payroll run. All amounts are clerk-supplied and pre-computed; the module performs
/// no withholding-table calculations. Number/Status/Id are server-assigned and never sent.</summary>
public sealed record RecordPayrollRunRequest(
    decimal Gross,
    decimal EmployeeFica,
    decimal EmployerFica,
    decimal Deductions,
    decimal IncomeTaxWithheld,
    DateOnly PayDate,
    string? Memo);

/// <summary>Record a tax remittance. The clerk supplies the amounts to pay down each liability; the
/// module performs no outstanding-balance tracking.</summary>
public sealed record RecordTaxRemittanceRequest(
    decimal WithholdingsAmount,
    decimal TaxesAmount,
    DateOnly PayDate,
    string? Memo);

/// <summary>Void a posted payroll run or tax remittance, with an optional reason.</summary>
public sealed record VoidReasonRequest(string? Reason);
