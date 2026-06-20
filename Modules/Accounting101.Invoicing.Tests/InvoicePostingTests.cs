using Accounting101.Invoicing;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Invoicing.Tests;

/// <summary>
/// The invoicing recipe: an invoice composes into one balanced entry — Dr A/R (total) tagged by
/// customer, Cr Revenue (subtotal), Cr Sales Tax Payable (tax) — back-linked to the invoice, with tax
/// computed off the taxable lines only, and a tax-exempt invoice collapsing to two lines.
/// </summary>
public sealed class InvoicePostingTests
{
    private static readonly InvoicePostingAccounts Accounts = new()
    {
        ReceivableAccountId = Guid.NewGuid(),
        RevenueAccountId = Guid.NewGuid(),
        SalesTaxPayableAccountId = Guid.NewGuid(),
    };

    private static decimal SignedEffect(PostLineRequest line) =>
        line.Direction == "Debit" ? line.Amount : -line.Amount;

    [Fact]
    public void An_invoice_composes_into_a_balanced_entry_split_across_revenue_and_tax()
    {
        var customer = Guid.NewGuid();
        Invoice invoice = new()
        {
            Id = Guid.NewGuid(),
            CustomerId = customer,
            Number = "INV-1001",
            IssueDate = new DateOnly(2026, 3, 31),
            TaxRate = 0.07m,
            Lines =
            [
                new InvoiceLine { Description = "Widgets", Quantity = 2m, UnitPrice = 50m },                 // 100, taxable
                new InvoiceLine { Description = "Consulting", Quantity = 1m, UnitPrice = 30m, Taxable = false }, // 30, exempt
            ],
        };

        // Subtotal 130, taxable base 100, tax 7.00, total 137.
        Assert.Equal(130m, invoice.Subtotal);
        Assert.Equal(7m, invoice.Tax);
        Assert.Equal(137m, invoice.Total);

        PostEntryRequest entry = InvoicePosting.Compose(invoice, Accounts);

        Assert.Equal(0m, entry.Lines.Sum(SignedEffect));         // balanced by construction
        Assert.Equal(invoice.Id, entry.SourceRef);               // back-linked to the invoice
        Assert.Equal("Invoice", entry.SourceType);
        Assert.Equal("INV-1001", entry.Reference);
        Assert.Equal(new DateOnly(2026, 3, 31), entry.EffectiveDate);

        PostLineRequest ar = entry.Lines.Single(l => l.AccountId == Accounts.ReceivableAccountId);
        Assert.Equal("Debit", ar.Direction);
        Assert.Equal(137m, ar.Amount);
        Assert.Equal(customer, ar.Dimensions!["Customer"]);      // A/R tagged so the subledger ties out

        Assert.Equal(130m, entry.Lines.Single(l => l.AccountId == Accounts.RevenueAccountId).Amount);
        Assert.Equal(7m, entry.Lines.Single(l => l.AccountId == Accounts.SalesTaxPayableAccountId).Amount);
    }

    [Fact]
    public void A_tax_exempt_invoice_composes_into_two_lines()
    {
        Invoice invoice = new()
        {
            Id = Guid.NewGuid(),
            CustomerId = Guid.NewGuid(),
            Number = "INV-1002",
            IssueDate = new DateOnly(2026, 3, 31),
            TaxRate = 0m,
            Lines = [new InvoiceLine { Description = "Services", Quantity = 1m, UnitPrice = 500m }],
        };

        PostEntryRequest entry = InvoicePosting.Compose(invoice, Accounts);

        Assert.Equal(2, entry.Lines.Count);                      // no tax line
        Assert.DoesNotContain(entry.Lines, l => l.AccountId == Accounts.SalesTaxPayableAccountId);
        Assert.Equal(0m, entry.Lines.Sum(SignedEffect));
        Assert.Equal(500m, entry.Lines.Single(l => l.AccountId == Accounts.ReceivableAccountId).Amount);
    }
}
