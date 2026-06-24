using Accounting101.Invoicing;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Invoicing.Tests;

public sealed class PaymentPostingTests
{
    private static readonly PaymentPostingAccounts Accounts = new()
    {
        ReceivableAccountId = Guid.NewGuid(),
        CashAccountId = Guid.NewGuid(),
        CustomerCreditsAccountId = Guid.NewGuid(),
    };

    private static decimal Signed(PostLineRequest l) => l.Direction == "Debit" ? l.Amount : -l.Amount;

    [Fact]
    public void Fully_allocated_payment_posts_cash_and_ar_only()
    {
        Guid customer = Guid.NewGuid();
        PaymentBody body = new(customer, new DateOnly(2026, 3, 31), 500m, null,
            [new Allocation(Guid.NewGuid(), 200m), new Allocation(Guid.NewGuid(), 300m)]);

        PostEntryRequest entry = PaymentPosting.ComposePayment(Guid.NewGuid(), body, Accounts);

        Assert.Equal(0m, entry.Lines.Sum(Signed));
        Assert.Equal(500m, entry.Lines.Single(l => l.AccountId == Accounts.CashAccountId).Amount);
        PostLineRequest ar = entry.Lines.Single(l => l.AccountId == Accounts.ReceivableAccountId);
        Assert.Equal(500m, ar.Amount);
        Assert.Equal("Credit", ar.Direction);
        Assert.Equal(customer, ar.Dimensions!["Customer"]);
        Assert.DoesNotContain(entry.Lines, l => l.AccountId == Accounts.CustomerCreditsAccountId);
        Assert.Equal("Payment", entry.SourceType);
    }

    [Fact]
    public void Over_payment_routes_the_remainder_to_customer_credits()
    {
        Guid customer = Guid.NewGuid();
        PaymentBody body = new(customer, new DateOnly(2026, 3, 31), 500m, null,
            [new Allocation(Guid.NewGuid(), 300m)]);

        PostEntryRequest entry = PaymentPosting.ComposePayment(Guid.NewGuid(), body, Accounts);

        Assert.Equal(0m, entry.Lines.Sum(Signed));
        Assert.Equal(300m, entry.Lines.Single(l => l.AccountId == Accounts.ReceivableAccountId).Amount);
        PostLineRequest credit = entry.Lines.Single(l => l.AccountId == Accounts.CustomerCreditsAccountId);
        Assert.Equal(200m, credit.Amount);
        Assert.Equal(customer, credit.Dimensions!["Customer"]);
    }

    [Fact]
    public void Pure_deposit_posts_cash_and_credit_only()
    {
        PaymentBody body = new(Guid.NewGuid(), new DateOnly(2026, 3, 31), 500m, null, []);

        PostEntryRequest entry = PaymentPosting.ComposePayment(Guid.NewGuid(), body, Accounts);

        Assert.Equal(0m, entry.Lines.Sum(Signed));
        Assert.Equal(2, entry.Lines.Count);
        Assert.Equal(500m, entry.Lines.Single(l => l.AccountId == Accounts.CustomerCreditsAccountId).Amount);
        Assert.DoesNotContain(entry.Lines, l => l.AccountId == Accounts.ReceivableAccountId);
    }
}
