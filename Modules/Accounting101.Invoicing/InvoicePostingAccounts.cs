namespace Accounting101.Invoicing;

/// <summary>
/// The chart accounts the invoicing recipe posts to — the module's "chart contract." These ids are
/// resolved from the client's chart at setup (A/R must be a control account requiring the Customer
/// dimension); the recipe itself just takes them, so it stays pure.
/// </summary>
public sealed record InvoicePostingAccounts
{
    /// <summary>Accounts Receivable — the control account, debited for the invoice total, tagged by customer.</summary>
    public required Guid ReceivableAccountId { get; init; }

    /// <summary>Revenue — credited for the pre-tax subtotal.</summary>
    public required Guid RevenueAccountId { get; init; }

    /// <summary>Sales Tax Payable — credited for the tax (a liability owed to the taxing authority).</summary>
    public required Guid SalesTaxPayableAccountId { get; init; }
}
