namespace Accounting101.Receivables;

/// <summary>
/// One line of commercial detail on an invoice. This detail stays in the module — only the line's
/// money rolls up into the journal entry — so the journal stays lean while the invoice keeps the story.
/// </summary>
public sealed record InvoiceLine
{
    public required string Description { get; init; }
    public required decimal Quantity { get; init; }
    public required decimal UnitPrice { get; init; }

    /// <summary>Whether this line is subject to sales tax. Defaults to taxable.</summary>
    public bool Taxable { get; init; } = true;

    /// <summary>
    /// Semantic revenue classification for this line (e.g. "License"). The posting recipe resolves it
    /// to a revenue account via the chart contract; <c>null</c> (or an unmapped category) credits the
    /// default revenue account. Orthogonal to <see cref="Taxable"/> — taxability is not classification.
    /// </summary>
    public string? RevenueCategory { get; init; }

    /// <summary>Extended amount for the line. Money is <see cref="decimal"/>, never floating point.</summary>
    public decimal Amount => Quantity * UnitPrice;
}
