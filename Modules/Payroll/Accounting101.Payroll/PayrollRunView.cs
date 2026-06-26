namespace Accounting101.Payroll;

/// <summary>A read model for a payroll run — the document plus any computed display fields.</summary>
public sealed record PayrollRunView(PayrollRun Run);
