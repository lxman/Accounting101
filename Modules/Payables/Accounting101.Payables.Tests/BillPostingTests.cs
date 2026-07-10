using Accounting101.Ledger.Contracts;
using Accounting101.Settlement;

namespace Accounting101.Payables.Tests;

public sealed class BillPostingTests
{
    private static decimal Signed(PostLineRequest l) => l.Direction == "Debit" ? l.Amount : -l.Amount;

    [Fact]
    public void A_bill_debits_each_expense_account_and_credits_ap()
    {
        Guid vendor = Guid.NewGuid(), rent = Guid.NewGuid(), utilities = Guid.NewGuid();
        BillPostingAccounts accounts = new() { PayableAccountId = Guid.NewGuid() };
        Bill bill = new()
        {
            Id = Guid.NewGuid(), VendorId = vendor, Number = "BILL-00001", BillDate = new DateOnly(2026, 3, 1),
            Status = BillStatus.Entered,
            Lines =
            [
                new BillLine { Description = "Rent", Amount = 6000m, ExpenseAccountId = rent },
                new BillLine { Description = "Utilities", Amount = 800m, ExpenseAccountId = utilities },
            ],
        };

        PostEntryRequest entry = BillPosting.ComposeBill(bill, accounts);

        Assert.Equal(0m, entry.Lines.Sum(Signed));
        Assert.Equal(6000m, entry.Lines.Single(l => l.AccountId == rent).Amount);
        Assert.Equal(800m, entry.Lines.Single(l => l.AccountId == utilities).Amount);
        PostLineRequest ap = entry.Lines.Single(l => l.AccountId == accounts.PayableAccountId);
        Assert.Equal("Credit", ap.Direction);
        Assert.Equal(6800m, ap.Amount);
        Assert.Equal(vendor, ap.Dimensions!["Vendor"]);
        Assert.Equal("Bill", entry.SourceType);
        Assert.Equal(bill.Id, entry.SourceRef);
    }

    [Fact]
    public void Lines_sharing_an_expense_account_collapse_into_one_debit()
    {
        Guid shared = Guid.NewGuid();
        BillPostingAccounts accounts = new() { PayableAccountId = Guid.NewGuid() };
        Bill bill = new()
        {
            Id = Guid.NewGuid(), VendorId = Guid.NewGuid(), BillDate = new DateOnly(2026, 3, 1), Status = BillStatus.Entered,
            Lines =
            [
                new BillLine { Description = "Utilities A", Amount = 300m, ExpenseAccountId = shared },
                new BillLine { Description = "Utilities B", Amount = 200m, ExpenseAccountId = shared },
            ],
        };

        PostEntryRequest entry = BillPosting.ComposeBill(bill, accounts);

        Assert.Equal(500m, entry.Lines.Single(l => l.AccountId == shared).Amount);
        Assert.Equal(2, entry.Lines.Count); // one expense debit + one A/P credit
        Assert.Equal(0m, entry.Lines.Sum(Signed));
    }

    [Fact]
    public void A_bill_payment_debits_ap_and_routes_overpayment_to_vendor_credits()
    {
        Guid vendor = Guid.NewGuid();
        BillPaymentPostingAccounts accounts = new()
        {
            PayableAccountId = Guid.NewGuid(), CashAccountId = Guid.NewGuid(), VendorCreditsAccountId = Guid.NewGuid(),
        };
        BillPaymentBody body = new(vendor, new DateOnly(2026, 3, 31), 500m, "check");
        Allocation[] allocations = [new Allocation(Guid.NewGuid(), 300m)];

        PostEntryRequest entry = BillPosting.ComposeBillPayment(Guid.NewGuid(), body, allocations, accounts);

        Assert.Equal(0m, entry.Lines.Sum(Signed));
        Assert.Equal(500m, entry.Lines.Single(l => l.AccountId == accounts.CashAccountId).Amount); // Cr Cash total
        Assert.Equal("Credit", entry.Lines.Single(l => l.AccountId == accounts.CashAccountId).Direction);
        PostLineRequest ap = entry.Lines.Single(l => l.AccountId == accounts.PayableAccountId);
        Assert.Equal("Debit", ap.Direction);
        Assert.Equal(300m, ap.Amount);
        Assert.Equal(vendor, ap.Dimensions!["Vendor"]);
        PostLineRequest credits = entry.Lines.Single(l => l.AccountId == accounts.VendorCreditsAccountId);
        Assert.Equal("Debit", credits.Direction);   // asset increases
        Assert.Equal(200m, credits.Amount);
        Assert.Equal(vendor, credits.Dimensions!["Vendor"]);
        Assert.Equal("BillPayment", entry.SourceType);
    }

    [Fact]
    public void A_vendor_credit_application_debits_ap_and_credits_vendor_credits()
    {
        Guid vendor = Guid.NewGuid();
        BillPaymentPostingAccounts accounts = new()
        {
            PayableAccountId = Guid.NewGuid(), CashAccountId = Guid.NewGuid(), VendorCreditsAccountId = Guid.NewGuid(),
        };
        VendorCreditApplicationBody body = new(vendor, new DateOnly(2026, 4, 2));
        Allocation[] allocations = [new Allocation(Guid.NewGuid(), 150m)];

        PostEntryRequest entry = BillPosting.ComposeVendorCreditApplication(Guid.NewGuid(), body, allocations, accounts);

        Assert.Equal(0m, entry.Lines.Sum(Signed));
        PostLineRequest ap = entry.Lines.Single(l => l.AccountId == accounts.PayableAccountId);
        PostLineRequest credits = entry.Lines.Single(l => l.AccountId == accounts.VendorCreditsAccountId);
        Assert.Equal("Debit", ap.Direction);
        Assert.Equal(150m, ap.Amount);
        Assert.Equal("Credit", credits.Direction); // asset decreases
        Assert.Equal(150m, credits.Amount);
        Assert.Equal(vendor, ap.Dimensions!["Vendor"]);
        Assert.Equal(vendor, credits.Dimensions!["Vendor"]);
        Assert.Equal("VendorCreditApplication", entry.SourceType);
    }

    [Fact]
    public void ComposeBill_sets_a_deterministic_id_from_source()
    {
        Guid rent = Guid.NewGuid();
        BillPostingAccounts accounts = new() { PayableAccountId = Guid.NewGuid() };
        Bill bill = new()
        {
            Id = Guid.NewGuid(), VendorId = Guid.NewGuid(), Number = "BILL-D1", BillDate = new DateOnly(2026, 6, 1),
            Status = BillStatus.Entered,
            Lines = [new BillLine { Description = "Rent", Amount = 1000m, ExpenseAccountId = rent }],
        };

        PostEntryRequest a = BillPosting.ComposeBill(bill, accounts);
        PostEntryRequest b = BillPosting.ComposeBill(bill, accounts);

        Assert.Equal(EntryIdentity.ForSource(BillPosting.BillSourceType, bill.Id), a.Id);
        Assert.Equal(a.Id, b.Id);
    }

    [Fact]
    public void ComposeBillPayment_sets_a_deterministic_id_from_source()
    {
        Guid paymentId = Guid.NewGuid();
        Guid vendor = Guid.NewGuid();
        BillPaymentPostingAccounts accounts = new()
        {
            PayableAccountId = Guid.NewGuid(), CashAccountId = Guid.NewGuid(), VendorCreditsAccountId = Guid.NewGuid(),
        };
        BillPaymentBody body = new(vendor, new DateOnly(2026, 6, 1), 200m, "check");
        Allocation[] allocations = [new Allocation(Guid.NewGuid(), 200m)];

        PostEntryRequest a = BillPosting.ComposeBillPayment(paymentId, body, allocations, accounts);
        PostEntryRequest b = BillPosting.ComposeBillPayment(paymentId, body, allocations, accounts);

        Assert.Equal(EntryIdentity.ForSource(BillPosting.BillPaymentSourceType, paymentId), a.Id);
        Assert.Equal(a.Id, b.Id);
    }

    [Fact]
    public void ComposeVendorCreditApplication_sets_a_deterministic_id_from_source()
    {
        Guid id = Guid.NewGuid();
        Guid vendor = Guid.NewGuid();
        BillPaymentPostingAccounts accounts = new()
        {
            PayableAccountId = Guid.NewGuid(), CashAccountId = Guid.NewGuid(), VendorCreditsAccountId = Guid.NewGuid(),
        };
        VendorCreditApplicationBody body = new(vendor, new DateOnly(2026, 6, 1));
        Allocation[] allocations = [new Allocation(Guid.NewGuid(), 75m)];

        PostEntryRequest a = BillPosting.ComposeVendorCreditApplication(id, body, allocations, accounts);
        PostEntryRequest b = BillPosting.ComposeVendorCreditApplication(id, body, allocations, accounts);

        Assert.Equal(EntryIdentity.ForSource(BillPosting.VendorCreditApplicationSourceType, id), a.Id);
        Assert.Equal(a.Id, b.Id);
    }
}
