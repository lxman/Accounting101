namespace Accounting101.Payroll;

/// <summary>The clerk-supplied inputs for a tax remittance payment. The clerk provides the amounts
/// to pay down each liability; the module performs no outstanding-balance tracking.</summary>
public sealed record TaxRemittanceBody(
    decimal WithholdingsAmount,
    decimal TaxesAmount,
    DateOnly PayDate,
    string? Memo);
