using Accounting101.Payables;
using Accounting101.Ledger.Contracts;

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
}
