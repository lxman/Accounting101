namespace Accounting101.Receivables;

/// <summary>The full read-only account view for one customer: header balances, AR aging, open invoices,
/// the AR running-balance statement, and the credit-activity ledger. Server-computed; nothing stored.</summary>
public sealed record CustomerAccountView(
    Customer Customer,
    decimal ArBalance,
    decimal CreditBalance,
    AgingBuckets Aging,
    IReadOnlyList<OpenInvoiceLine> OpenInvoices,
    IReadOnlyList<StatementLine> StatementLines,
    IReadOnlyList<CreditActivityLine> CreditLines);

/// <summary>Open AR bucketed by days past due. Sums to <see cref="CustomerAccountView.ArBalance"/>.</summary>
public sealed record AgingBuckets(decimal Current, decimal D1To30, decimal D31To60, decimal D61To90, decimal D90Plus);

/// <summary>One open (issued, not fully settled) invoice with its age.</summary>
public sealed record OpenInvoiceLine(Guid InvoiceId, string? Number, DateOnly IssueDate, DateOnly? DueDate, decimal OpenBalance, int DaysOverdue);

/// <summary>One AR statement line. Charge increases the running balance; Payment decreases it.</summary>
public sealed record StatementLine(DateOnly Date, string Type, string? Reference, decimal Charge, decimal Payment, decimal Balance);

/// <summary>One credit-ledger line. Amount is signed (+ overpayment, − application/refund); CreditBalance is the running total.</summary>
public sealed record CreditActivityLine(DateOnly Date, string Type, string? Reference, decimal Amount, decimal CreditBalance);
