using Accounting101.Receivables;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Receivables.Tests;

/// <summary>
/// The receivables recipe: an invoice composes into one balanced entry — Dr A/R (total) tagged by
/// customer, Cr Revenue (subtotal), Cr Sales Tax Payable (tax) — back-linked to the invoice, with tax
/// computed off the taxable lines only, and a tax-exempt invoice collapsing to two lines.
/// </summary>
public sealed class InvoicePostingTests
{
    private static readonly InvoicePostingAccounts Accounts = new()
    {
        ReceivableAccountId = Guid.NewGuid(),
        DefaultRevenueAccountId = Guid.NewGuid(),
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

        Assert.Equal(130m, entry.Lines.Single(l => l.AccountId == Accounts.DefaultRevenueAccountId).Amount);
        Assert.Equal(7m, entry.Lines.Single(l => l.AccountId == Accounts.SalesTaxPayableAccountId).Amount);
    }

    [Fact]
    public void Lines_split_revenue_across_their_mapped_accounts()
    {
        Guid licenseRevenue = Guid.NewGuid();
        InvoicePostingAccounts accounts = new()
        {
            ReceivableAccountId = Guid.NewGuid(),
            DefaultRevenueAccountId = Guid.NewGuid(),
            RevenueAccountsByCategory = new Dictionary<string, Guid> { ["License"] = licenseRevenue },
            SalesTaxPayableAccountId = Guid.NewGuid(),
        };

        Invoice invoice = new()
        {
            Id = Guid.NewGuid(),
            CustomerId = Guid.NewGuid(),
            Number = "INV-2001",
            IssueDate = new DateOnly(2026, 3, 31),
            TaxRate = 0.08m,
            Lines =
            [
                new InvoiceLine { Description = "Consulting", Quantity = 1m, UnitPrice = 9250m, Taxable = false },      // default revenue
                new InvoiceLine { Description = "Software license", Quantity = 1m, UnitPrice = 2000m, RevenueCategory = "License" }, // mapped, taxable
            ],
        };

        // Subtotal 11250, taxable base 2000, tax 160.00, total 11410.
        PostEntryRequest entry = InvoicePosting.Compose(invoice, accounts);

        Assert.Equal(0m, entry.Lines.Sum(SignedEffect));                                                       // balances by construction
        Assert.Equal(9250m, entry.Lines.Single(l => l.AccountId == accounts.DefaultRevenueAccountId).Amount);  // consulting -> default
        Assert.Equal(2000m, entry.Lines.Single(l => l.AccountId == licenseRevenue).Amount);                    // license -> mapped account
        Assert.Equal(160m, entry.Lines.Single(l => l.AccountId == accounts.SalesTaxPayableAccountId).Amount);  // tax untouched by the split
        Assert.Equal(11410m, entry.Lines.Single(l => l.AccountId == accounts.ReceivableAccountId).Amount);     // A/R = total
    }

    [Fact]
    public void An_unmapped_category_folds_into_the_default_revenue_account()
    {
        InvoicePostingAccounts accounts = new()
        {
            ReceivableAccountId = Guid.NewGuid(),
            DefaultRevenueAccountId = Guid.NewGuid(),
            RevenueAccountsByCategory = new Dictionary<string, Guid> { ["License"] = Guid.NewGuid() },
            SalesTaxPayableAccountId = Guid.NewGuid(),
        };

        Invoice invoice = new()
        {
            Id = Guid.NewGuid(),
            CustomerId = Guid.NewGuid(),
            Number = "INV-2002",
            IssueDate = new DateOnly(2026, 3, 31),
            TaxRate = 0m,
            Lines =
            [
                new InvoiceLine { Description = "Consulting", Quantity = 1m, UnitPrice = 400m, Taxable = false },
                new InvoiceLine { Description = "Training", Quantity = 1m, UnitPrice = 100m, Taxable = false, RevenueCategory = "Training" }, // category not in the map
            ],
        };

        PostEntryRequest entry = InvoicePosting.Compose(invoice, accounts);

        // Both lines resolve to the default account → one revenue credit of 500, no license line.
        Assert.Equal(500m, entry.Lines.Single(l => l.AccountId == accounts.DefaultRevenueAccountId).Amount);
        Assert.Equal(2, entry.Lines.Count); // A/R + one revenue credit (tax-exempt)
        Assert.Equal(0m, entry.Lines.Sum(SignedEffect));
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
