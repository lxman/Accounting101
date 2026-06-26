namespace Accounting101.Payroll;

/// <summary>A read model for a tax remittance — the document plus any computed display fields.</summary>
public sealed record TaxRemittanceView(TaxRemittance Remittance);
