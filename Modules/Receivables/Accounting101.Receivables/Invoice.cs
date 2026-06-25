namespace Accounting101.Receivables;

/// <summary>
/// An invoice: the commercial document the module owns. Its money totals roll up into one balanced
/// journal entry when the invoice is issued; the per-line detail stays here. v1 carries a single tax
/// rate applied to the taxable lines — multi-jurisdiction tax is deferred.
/// </summary>
public sealed record Invoice
{
    public required Guid Id { get; init; }
    public required Guid CustomerId { get; init; }

    /// <summary>Human-facing invoice number, derived from the engine's gapless sequence at issue.
    /// Null while the invoice is a draft (a draft consumes no number).</summary>
    public string? Number { get; init; }

    public required DateOnly IssueDate { get; init; }
    public DateOnly? DueDate { get; init; }
    public InvoiceStatus Status { get; init; } = InvoiceStatus.Draft;

    /// <summary>Sales-tax rate applied to the taxable lines (e.g. 0.07 for 7%). Zero for a tax-exempt invoice.</summary>
    public decimal TaxRate { get; init; }

    public string? Memo { get; init; }
    public required IReadOnlyList<InvoiceLine> Lines { get; init; }

    /// <summary>The pre-tax total of every line.</summary>
    public decimal Subtotal => Lines.Sum(l => l.Amount);

    /// <summary>The portion of the subtotal that is taxable.</summary>
    public decimal TaxableBase => Lines.Where(l => l.Taxable).Sum(l => l.Amount);

    /// <summary>Sales tax, rounded to the cent (half away from zero — the common sales-tax convention).</summary>
    public decimal Tax => decimal.Round(TaxRate * TaxableBase, 2, MidpointRounding.AwayFromZero);

    /// <summary>What the customer owes: subtotal plus tax. This is the receivable.</summary>
    public decimal Total => Subtotal + Tax;
}
