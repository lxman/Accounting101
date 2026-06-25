using Accounting101.Ledger.Contracts;

namespace Accounting101.Receivables;

/// <summary>
/// The receivables recipe: turns an issued invoice into the one balanced journal entry it posts —
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
        ];

        // Credit revenue per resolved account: each line's category maps to an account (a null or
        // unmapped category folds into the default), and lines sharing an account collapse into one
        // credit. Grouping by exact line-sum keeps the credits summing to the subtotal with no rounding
        // residue. Ordered by account id so the entry is deterministic.
        lines.AddRange(invoice.Lines
            .GroupBy(line => ResolveRevenueAccount(line, accounts))
            .OrderBy(group => group.Key)
            .Select(group => new PostLineRequest(group.Key, "Credit", group.Sum(line => line.Amount))));

        // Only split out tax when there is any — a tax-exempt invoice carries no tax line.
        if (invoice.Tax != 0m)
            lines.Add(new(accounts.SalesTaxPayableAccountId, "Credit", invoice.Tax));

        return new PostEntryRequest(
            Id: EntryIdentity.ForSource(SourceType, invoice.Id),
            EffectiveDate: invoice.IssueDate,
            Reference: invoice.Number,
            Memo: invoice.Memo,
            Lines: lines,
            SourceRef: invoice.Id,
            SourceType: SourceType);
    }

    /// <summary>Resolve a line's revenue account: its mapped category, or the default for a null/unmapped category.</summary>
    private static Guid ResolveRevenueAccount(InvoiceLine line, InvoicePostingAccounts accounts) =>
        line.RevenueCategory is { } category && accounts.RevenueAccountsByCategory.TryGetValue(category, out Guid account)
            ? account
            : accounts.DefaultRevenueAccountId;
}
