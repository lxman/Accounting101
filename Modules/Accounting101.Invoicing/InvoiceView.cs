namespace Accounting101.Invoicing;

/// <summary>An invoice plus its derived settlement facet — what a read endpoint returns.</summary>
public sealed record InvoiceView(Invoice Invoice, decimal OpenBalance, SettlementStatus SettlementStatus);
