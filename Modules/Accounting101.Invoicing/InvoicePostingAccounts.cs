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

    /// <summary>Revenue — credited for any line whose category is null or unmapped. The fallback account.</summary>
    public required Guid DefaultRevenueAccountId { get; init; }

    /// <summary>
    /// Revenue classification → account. A line's <see cref="InvoiceLine.RevenueCategory"/> resolves
    /// through this map; misses fall back to <see cref="DefaultRevenueAccountId"/>. Empty by default,
    /// which makes every line credit the default account (the original single-revenue behavior).
    /// </summary>
    public IReadOnlyDictionary<string, Guid> RevenueAccountsByCategory { get; init; }
        = new Dictionary<string, Guid>();

    /// <summary>Sales Tax Payable — credited for the tax (a liability owed to the taxing authority).</summary>
    public required Guid SalesTaxPayableAccountId { get; init; }
}
