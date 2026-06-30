using Accounting101.Settlement;

namespace Accounting101.Payables.Tests;

public sealed class VendorAccountBuilderTests
{
    private static Bill EnteredBill(Guid id, decimal amount, DateOnly billDate, DateOnly? due, string? number = "B-1") =>
        new()
        {
            Id = id, VendorId = Guid.NewGuid(), Number = number, BillDate = billDate, DueDate = due,
            Status = BillStatus.Entered, Lines = [new BillLine { Description = "x", Amount = amount, ExpenseAccountId = Guid.NewGuid() }],
        };

    private static BillPayment Payment(Guid billId, decimal amount, decimal alloc, DateOnly date, bool voided = false) =>
        new() { Id = Guid.NewGuid(), VendorId = Guid.NewGuid(), Date = date, Amount = amount, Method = null,
                Allocations = [new Allocation(billId, alloc)], Voided = voided };

    private static VendorCreditApplication CreditApp(Guid billId, decimal alloc, DateOnly date, bool voided = false) =>
        new() { Id = Guid.NewGuid(), VendorId = Guid.NewGuid(), Date = date, Allocations = [new Allocation(billId, alloc)], Voided = voided };

    [Fact]
    public void AppliedByBill_sums_nonvoided_payment_and_credit_allocations()
    {
        Guid bill = Guid.NewGuid();
        var applied = VendorAccountBuilder.AppliedByBill(
            [Payment(bill, 100m, 80m, new DateOnly(2026, 3, 1)), Payment(bill, 50m, 50m, new DateOnly(2026, 3, 2), voided: true)],
            [CreditApp(bill, 20m, new DateOnly(2026, 3, 3))]);
        Assert.Equal(100m, applied[bill]); // 80 + 20; the voided 50 excluded
    }

    [Fact]
    public void OpenBills_keeps_only_entered_with_positive_open_oldest_first_with_overdue()
    {
        Guid b1 = Guid.NewGuid(), b2 = Guid.NewGuid();
        var bills = new List<Bill> {
            EnteredBill(b2, 100m, new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28)),  // older billDate
            EnteredBill(b1, 100m, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31)),
        };
        var applied = new Dictionary<Guid, decimal> { [b1] = 100m }; // b1 fully paid → excluded
        var open = VendorAccountBuilder.OpenBills(bills, applied, new DateOnly(2026, 4, 15));
        Assert.Single(open);
        Assert.Equal(b2, open[0].BillId);
        Assert.Equal(100m, open[0].OpenBalance);
        Assert.Equal(46, open[0].DaysOverdue); // 2026-02-28 → 2026-04-15
    }

    [Fact]
    public void Aging_buckets_by_fencepost()
    {
        OpenBillLine L(int overdue) => new(Guid.NewGuid(), null, new DateOnly(2026, 1, 1), null, 10m, overdue);
        var aging = VendorAccountBuilder.Aging([L(0), L(1), L(30), L(31), L(60), L(61), L(90), L(91)]);
        Assert.Equal(10m, aging.Current);  // overdue 0
        Assert.Equal(20m, aging.D1To30);   // 1, 30
        Assert.Equal(20m, aging.D31To60);  // 31, 60
        Assert.Equal(20m, aging.D61To90);  // 61, 90
        Assert.Equal(10m, aging.D90Plus);  // 91
    }

    [Fact]
    public void Statement_orders_charges_before_settlements_same_date_and_ends_at_ap_balance()
    {
        Guid bill = Guid.NewGuid();
        var bills = new List<Bill> { EnteredBill(bill, 100m, new DateOnly(2026, 3, 1), null) };
        var lines = VendorAccountBuilder.Statement(
            bills, [Payment(bill, 30m, 30m, new DateOnly(2026, 3, 1))], []);
        Assert.Equal("Bill", lines[0].Type);     // charge first
        Assert.Equal(100m, lines[0].Balance);
        Assert.Equal("Payment", lines[1].Type);  // settlement second (same date)
        Assert.Equal(70m, lines[1].Balance);     // running AP = 100 - 30
    }

    [Fact]
    public void CreditActivity_overpayment_plus_application_minus_running_balance()
    {
        Guid bill = Guid.NewGuid();
        // payment of 150 allocating 100 → 50 overpayment; later credit application of 20.
        var lines = VendorAccountBuilder.CreditActivity(
            [Payment(bill, 150m, 100m, new DateOnly(2026, 3, 1))],
            [CreditApp(bill, 20m, new DateOnly(2026, 3, 5))]);
        Assert.Equal("Overpayment", lines[0].Type);
        Assert.Equal(50m, lines[0].Amount);
        Assert.Equal(50m, lines[0].CreditBalance);
        Assert.Equal(-20m, lines[1].Amount);
        Assert.Equal(30m, lines[1].CreditBalance); // 50 - 20
    }
}
