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

    // BillPayment/VendorCreditApplication no longer store an allocation array — Statement/CreditActivity
    // key relief off a document's Id via the separately fold-sourced reliefByDocument dictionary instead.
    private static BillPayment Payment(decimal amount, DateOnly date, bool voided = false) =>
        new() { Id = Guid.NewGuid(), VendorId = Guid.NewGuid(), Date = date, Amount = amount, Method = null, Voided = voided };

    private static VendorCreditApplication CreditApp(DateOnly date, bool voided = false) =>
        new() { Id = Guid.NewGuid(), VendorId = Guid.NewGuid(), Date = date, Voided = voided };

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
        BillPayment payment = Payment(30m, new DateOnly(2026, 3, 1));
        // reliefByDocument is fold-sourced in production (VendorAccountService); a plain dictionary
        // stands in for it here — the builder itself never touches Allocations.
        var relief = new Dictionary<Guid, decimal> { [payment.Id] = 30m };
        var lines = VendorAccountBuilder.Statement(bills, [payment], [], relief);
        Assert.Equal("Bill", lines[0].Type);     // charge first
        Assert.Equal(100m, lines[0].Balance);
        Assert.Equal("Payment", lines[1].Type);  // settlement second (same date)
        Assert.Equal(70m, lines[1].Balance);     // running AP = 100 - 30
    }

    [Fact]
    public void CreditActivity_overpayment_plus_application_minus_running_balance()
    {
        // payment of 150 relieving 100 → 50 overpayment; later credit application relieving 20.
        BillPayment payment = Payment(150m, new DateOnly(2026, 3, 1));
        VendorCreditApplication creditApp = CreditApp(new DateOnly(2026, 3, 5));
        var relief = new Dictionary<Guid, decimal> { [payment.Id] = 100m, [creditApp.Id] = 20m };
        var lines = VendorAccountBuilder.CreditActivity([payment], [creditApp], relief);
        Assert.Equal("Overpayment", lines[0].Type);
        Assert.Equal(50m, lines[0].Amount);
        Assert.Equal(50m, lines[0].CreditBalance);
        Assert.Equal(-20m, lines[1].Amount);
        Assert.Equal(30m, lines[1].CreditBalance); // 50 - 20
    }

    [Fact]
    public void OpenBills_orders_same_date_by_number()
    {
        Bill b1002 = EnteredBill(Guid.NewGuid(), 100m, new DateOnly(2026, 3, 1), null, "B-1002");
        Bill b1001 = EnteredBill(Guid.NewGuid(), 100m, new DateOnly(2026, 3, 1), null, "B-1001");
        var open = VendorAccountBuilder.OpenBills([b1002, b1001], new Dictionary<Guid, decimal>(), new DateOnly(2026, 3, 1));

        Assert.Equal(["B-1001", "B-1002"], open.Select(l => l.Number));
    }

    [Fact]
    public void CreditActivity_orders_same_date_deterministically_by_type_then_id()
    {
        DateOnly d = new(2026, 3, 5);
        Guid pA = new("00000000-0000-0000-0000-000000000001");
        Guid pB = new("00000000-0000-0000-0000-000000000002");
        // Two same-date overpayments fed high-Id first + a same-date vendor-credit application.
        Guid appId = Guid.NewGuid();
        List<BillPayment> payments =
        [
            new() { Id = pB, VendorId = Guid.NewGuid(), Date = d, Amount = 20m, Method = null }, // unapplied 20 (no relief)
            new() { Id = pA, VendorId = Guid.NewGuid(), Date = d, Amount = 10m, Method = null }, // unapplied 10 (no relief)
        ];
        List<VendorCreditApplication> apps =
            [new() { Id = appId, VendorId = Guid.NewGuid(), Date = d }];
        Dictionary<Guid, decimal> relief = new() { [appId] = 5m };

        var lines = VendorAccountBuilder.CreditActivity(payments, apps, relief);

        Assert.Equal([10m, 20m, -5m], lines.Select(l => l.Amount));
        Assert.Equal(["Overpayment", "Overpayment", "Credit applied"], lines.Select(l => l.Type));
    }
}
