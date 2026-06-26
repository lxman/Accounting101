namespace Accounting101.Payroll;

/// <summary>An evidentiary record of a payroll run — posted in one step, voidable, never drafted.
/// Number and status are derived from the engine's document envelope; the module stores no identity
/// of its own.</summary>
public sealed record PayrollRun
{
    public required Guid Id { get; init; }
    public string? Number { get; init; }
    public required decimal Gross { get; init; }
    public required decimal EmployeeFica { get; init; }
    public required decimal EmployerFica { get; init; }
    public required decimal Deductions { get; init; }
    public required decimal IncomeTaxWithheld { get; init; }
    public required DateOnly PayDate { get; init; }
    public string? Memo { get; init; }
    public PayrollRunStatus Status { get; init; } = PayrollRunStatus.Posted;
}
