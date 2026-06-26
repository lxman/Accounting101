namespace Accounting101.Payroll;

/// <summary>The clerk-supplied inputs for a payroll run. All amounts are pre-computed; the module
/// performs no withholding-table calculations.</summary>
public sealed record PayrollRunBody(
    decimal Gross,
    decimal EmployeeFica,
    decimal EmployerFica,
    decimal Deductions,
    decimal IncomeTaxWithheld,
    DateOnly PayDate,
    string? Memo);
