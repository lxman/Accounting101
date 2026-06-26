using Accounting101.Ledger.Contracts;
using Accounting101.Settlement;

namespace Accounting101.Receivables.Tests;

public sealed class PaymentPostingTests
{
    private static PaymentPostingAccounts Accounts() => new()
    {
        ReceivableAccountId = Guid.NewGuid(),
        CashAccountId = Guid.NewGuid(),
        CustomerCreditsAccountId = Guid.NewGuid(),
        BadDebtExpenseAccountId = Guid.NewGuid(),
        SalesReturnsAccountId = Guid.NewGuid(),
    };

    private static decimal Signed(PostLineRequest l) => l.Direction == "Debit" ? l.Amount : -l.Amount;

    [Fact]
    public void Fully_allocated_payment_posts_cash_and_ar_only()
    {
        PaymentPostingAccounts acc = Accounts();
        Guid customer = Guid.NewGuid();
        PaymentBody body = new(customer, new DateOnly(2026, 3, 31), 500m, null,
            [new Allocation(Guid.NewGuid(), 200m), new Allocation(Guid.NewGuid(), 300m)]);

        PostEntryRequest entry = PaymentPosting.ComposePayment(Guid.NewGuid(), body, acc);

        Assert.Equal(0m, entry.Lines.Sum(Signed));
        Assert.Equal(500m, entry.Lines.Single(l => l.AccountId == acc.CashAccountId).Amount);
        PostLineRequest ar = entry.Lines.Single(l => l.AccountId == acc.ReceivableAccountId);
        Assert.Equal(500m, ar.Amount);
        Assert.Equal("Credit", ar.Direction);
        Assert.Equal(customer, ar.Dimensions!["Customer"]);
        Assert.DoesNotContain(entry.Lines, l => l.AccountId == acc.CustomerCreditsAccountId);
        Assert.Equal("Payment", entry.SourceType);
    }

    [Fact]
    public void Over_payment_routes_the_remainder_to_customer_credits()
    {
        PaymentPostingAccounts acc = Accounts();
        Guid customer = Guid.NewGuid();
        PaymentBody body = new(customer, new DateOnly(2026, 3, 31), 500m, null,
            [new Allocation(Guid.NewGuid(), 300m)]);

        PostEntryRequest entry = PaymentPosting.ComposePayment(Guid.NewGuid(), body, acc);

        Assert.Equal(0m, entry.Lines.Sum(Signed));
        Assert.Equal(300m, entry.Lines.Single(l => l.AccountId == acc.ReceivableAccountId).Amount);
        PostLineRequest credit = entry.Lines.Single(l => l.AccountId == acc.CustomerCreditsAccountId);
        Assert.Equal(200m, credit.Amount);
        Assert.Equal(customer, credit.Dimensions!["Customer"]);
    }

    [Fact]
    public void Pure_deposit_posts_cash_and_credit_only()
    {
        PaymentPostingAccounts acc = Accounts();
        PaymentBody body = new(Guid.NewGuid(), new DateOnly(2026, 3, 31), 500m, null, []);

        PostEntryRequest entry = PaymentPosting.ComposePayment(Guid.NewGuid(), body, acc);

        Assert.Equal(0m, entry.Lines.Sum(Signed));
        Assert.Equal(2, entry.Lines.Count);
        Assert.Equal(500m, entry.Lines.Single(l => l.AccountId == acc.CustomerCreditsAccountId).Amount);
        Assert.DoesNotContain(entry.Lines, l => l.AccountId == acc.ReceivableAccountId);
    }

    [Fact]
    public void Credit_application_moves_customer_credits_to_ar()
    {
        PaymentPostingAccounts acc = Accounts();
        Guid customer = Guid.NewGuid();
        CreditApplicationBody body = new(customer, new DateOnly(2026, 4, 1),
            [new Allocation(Guid.NewGuid(), 120m), new Allocation(Guid.NewGuid(), 80m)]);

        PostEntryRequest entry = PaymentPosting.ComposeCreditApplication(Guid.NewGuid(), body, acc);

        Assert.Equal(0m, entry.Lines.Sum(Signed));
        PostLineRequest debit = entry.Lines.Single(l => l.AccountId == acc.CustomerCreditsAccountId);
        PostLineRequest credit = entry.Lines.Single(l => l.AccountId == acc.ReceivableAccountId);
        Assert.Equal("Debit", debit.Direction);
        Assert.Equal(200m, debit.Amount);
        Assert.Equal("Credit", credit.Direction);
        Assert.Equal(200m, credit.Amount);
        Assert.Equal(customer, debit.Dimensions!["Customer"]);
        Assert.Equal(customer, credit.Dimensions!["Customer"]);
        Assert.Equal("CreditApplication", entry.SourceType);
    }

    [Fact]
    public void ComposeWriteOff_debits_bad_debt_credits_receivable_balanced()
    {
        PaymentPostingAccounts acc = Accounts();
        Guid customer = Guid.NewGuid();
        Guid invoice = Guid.NewGuid();
        WriteOffBody body = new(customer, new DateOnly(2026, 3, 1), [new Allocation(invoice, 250m)], "uncollectible");

        PostEntryRequest entry = PaymentPosting.ComposeWriteOff(Guid.NewGuid(), body, acc);

        Assert.Equal("WriteOff", entry.SourceType);
        PostLineRequest debit = entry.Lines.Single(l => l.Direction == "Debit");
        PostLineRequest credit = entry.Lines.Single(l => l.Direction == "Credit");
        Assert.Equal(acc.BadDebtExpenseAccountId, debit.AccountId);
        Assert.Equal(250m, debit.Amount);
        Assert.Null(debit.Dimensions);                                  // expense line carries no Customer dim
        Assert.Equal(acc.ReceivableAccountId, credit.AccountId);
        Assert.Equal(250m, credit.Amount);
        Assert.Equal(customer, credit.Dimensions!["Customer"]);          // A/R credit carries the Customer dim
        Assert.Equal(entry.Lines.Where(l => l.Direction == "Debit").Sum(l => l.Amount),
                     entry.Lines.Where(l => l.Direction == "Credit").Sum(l => l.Amount));
    }

    [Fact]
    public void ComposeCreditNote_debits_sales_returns_credits_receivable()
    {
        PaymentPostingAccounts acc = Accounts();
        Guid customer = Guid.NewGuid();
        CreditNoteBody body = new(customer, new DateOnly(2026, 3, 1), [new Allocation(Guid.NewGuid(), 40m)], "return");

        PostEntryRequest entry = PaymentPosting.ComposeCreditNote(Guid.NewGuid(), body, acc);

        Assert.Equal("CreditNote", entry.SourceType);
        Assert.Equal(acc.SalesReturnsAccountId, entry.Lines.Single(l => l.Direction == "Debit").AccountId);
        PostLineRequest credit = entry.Lines.Single(l => l.Direction == "Credit");
        Assert.Equal(acc.ReceivableAccountId, credit.AccountId);
        Assert.Equal(customer, credit.Dimensions!["Customer"]);
        Assert.Equal(40m, credit.Amount);
    }

    [Fact]
    public void ComposeRefund_debits_customer_credits_credits_cash()
    {
        PaymentPostingAccounts acc = Accounts();
        Guid customer = Guid.NewGuid();
        RefundBody body = new(customer, new DateOnly(2026, 3, 1), 75m, "overpayment returned");

        PostEntryRequest entry = PaymentPosting.ComposeRefund(Guid.NewGuid(), body, acc);

        Assert.Equal("Refund", entry.SourceType);
        PostLineRequest debit = entry.Lines.Single(l => l.Direction == "Debit");
        PostLineRequest credit = entry.Lines.Single(l => l.Direction == "Credit");
        Assert.Equal(acc.CustomerCreditsAccountId, debit.AccountId);
        Assert.Equal(customer, debit.Dimensions!["Customer"]);           // Customer Credits draw-down carries the dim
        Assert.Equal(acc.CashAccountId, credit.AccountId);
        Assert.Null(credit.Dimensions);                                  // Cash carries no dim
        Assert.Equal(75m, debit.Amount);
        Assert.Equal(75m, credit.Amount);
    }

    [Fact]
    public void Recipes_carry_deterministic_distinct_source_ids()
    {
        PaymentPostingAccounts acc = Accounts();
        Guid docId = Guid.NewGuid();
        Guid customer = Guid.NewGuid();
        DateOnly d = new(2026, 3, 1);

        Guid? payment = PaymentPosting.ComposePayment(docId, new PaymentBody(customer, d, 10m, null, []), acc).Id;
        Guid? credit  = PaymentPosting.ComposeCreditApplication(docId, new CreditApplicationBody(customer, d, [new Allocation(Guid.NewGuid(), 10m)]), acc).Id;
        Guid? wo      = PaymentPosting.ComposeWriteOff(docId, new WriteOffBody(customer, d, [new Allocation(Guid.NewGuid(), 10m)], null), acc).Id;
        Guid? note    = PaymentPosting.ComposeCreditNote(docId, new CreditNoteBody(customer, d, [new Allocation(Guid.NewGuid(), 10m)], null), acc).Id;
        Guid? refund  = PaymentPosting.ComposeRefund(docId, new RefundBody(customer, d, 10m, null), acc).Id;

        Guid?[] ids = [payment, credit, wo, note, refund];
        Assert.All(ids, id => Assert.NotNull(id));                       // idempotency retrofit: no more Id: null
        Assert.Equal(5, ids.Distinct().Count());                        // same doc id, different source type → distinct entry id
        Assert.Equal(EntryIdentity.ForSource("WriteOff", docId), wo);   // deterministic
    }
}
