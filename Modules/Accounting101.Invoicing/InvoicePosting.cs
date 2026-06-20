using Accounting101.Ledger.Contracts;

namespace Accounting101.Invoicing;

/// <summary>
/// The invoicing recipe: turns an issued invoice into the one balanced journal entry it posts —
/// <c>Dr A/R (total), Cr Revenue (subtotal), Cr Sales Tax Payable (tax)</c>. The A/R line carries the
/// customer as a dimension (so the engine's A/R-by-customer subledger ties out), and the entry is
/// back-linked to the invoice via SourceRef/SourceType. Pure: it produces the wire contract and nothing
/// more, leaving sequencing, approval, and persistence to the engine. This is the whole "macro."
/// </summary>
public static class InvoicePosting
{
    /// <summary>The source-document discriminator stamped on every entry this module produces.</summary>
    public const string SourceType = "Invoice";

    /// <summary>The dimension type the A/R control account requires, and the recipe tags.</summary>
    public const string CustomerDimension = "Customer";

    public static PostEntryRequest Compose(Invoice invoice, InvoicePostingAccounts accounts)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        ArgumentNullException.ThrowIfNull(accounts);

        List<PostLineRequest> lines =
        [
            new(accounts.ReceivableAccountId, "Debit", invoice.Total,
                Dimensions: new Dictionary<string, Guid> { [CustomerDimension] = invoice.CustomerId }),
            new(accounts.RevenueAccountId, "Credit", invoice.Subtotal),
        ];

        // Only split out tax when there is any — a tax-exempt invoice is a clean two-line entry.
        if (invoice.Tax != 0m)
            lines.Add(new(accounts.SalesTaxPayableAccountId, "Credit", invoice.Tax));

        return new PostEntryRequest(
            Id: null,
            EffectiveDate: invoice.IssueDate,
            Reference: invoice.Number,
            Memo: invoice.Memo,
            Lines: lines,
            SourceRef: invoice.Id,
            SourceType: SourceType);
    }
}
