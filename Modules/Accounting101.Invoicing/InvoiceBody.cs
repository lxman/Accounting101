namespace Accounting101.Invoicing;

/// <summary>One stored invoice line — the clean body shape (no computed <see cref="InvoiceLine.Amount"/>),
/// so the opaque body round-trips through the document store.</summary>
public sealed record LineBody(string Description, decimal Quantity, decimal UnitPrice, bool Taxable, string? RevenueCategory = null);

/// <summary>
/// The stored shape of an invoice — commercial content only. Number and status are NOT stored; they are
/// derived from the engine's envelope (sequence → number, state → status). Computed totals stay on the
/// domain <see cref="Invoice"/>.
/// </summary>
public sealed record InvoiceBody(
    Guid CustomerId,
    DateOnly IssueDate,
    DateOnly? DueDate,
    decimal TaxRate,
    string? Memo,
    IReadOnlyList<LineBody> Lines);
