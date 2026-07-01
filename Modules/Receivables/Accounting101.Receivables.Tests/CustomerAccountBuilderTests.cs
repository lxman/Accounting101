using Accounting101.Receivables;
using Accounting101.Settlement;

namespace Accounting101.Receivables.Tests;

/// <summary>Pure-function tests for the customer-account folds: applied-per-invoice, open invoices + age,
/// aging buckets, the AR statement running balance, and the credit-activity ledger. No host needed.</summary>
public sealed class CustomerAccountBuilderTests
{
    private static Invoice IssuedInvoice(Guid id, string number, DateOnly issue, DateOnly? due, decimal amount) => new()
    {
        Id = id, CustomerId = Guid.NewGuid(), Number = number, IssueDate = issue, DueDate = due,
        Status = InvoiceStatus.Issued, TaxRate = 0m,
        Lines = [new InvoiceLine { Description = "x", Quantity = 1m, UnitPrice = amount, Taxable = false }],
    };

    [Fact]
    public void AppliedByInvoice_sums_nonvoided_allocations_across_doc_types()
    {
        Guid inv = Guid.NewGuid();
        List<Payment> payments =
        [
            new() { Id = Guid.NewGuid(), CustomerId = Guid.NewGuid(), Date = new(2026, 3, 2), Amount = 40m, Allocations = [new Allocation(inv, 40m)] },
            new() { Id = Guid.NewGuid(), CustomerId = Guid.NewGuid(), Date = new(2026, 3, 3), Amount = 10m, Allocations = [new Allocation(inv, 10m)], Voided = true },
        ];
        List<CreditNote> notes = [new() { Id = Guid.NewGuid(), CustomerId = Guid.NewGuid(), Date = new(2026, 3, 4), Allocations = [new Allocation(inv, 25m)] }];

        Dictionary<Guid, decimal> applied = CustomerAccountBuilder.AppliedByInvoice(payments, [], [], notes);

        Assert.Equal(65m, applied[inv]);   // 40 (payment) + 25 (note); voided 10 excluded
    }

    [Fact]
    public void OpenInvoices_computes_open_balance_and_days_overdue()
    {
        Guid inv = Guid.NewGuid();
        Invoice invoice = IssuedInvoice(inv, "1001", new(2026, 3, 1), new(2026, 3, 31), 100m);
        Dictionary<Guid, decimal> applied = new() { [inv] = 30m };

        IReadOnlyList<OpenInvoiceLine> open = CustomerAccountBuilder.OpenInvoices([invoice], applied, asOf: new(2026, 4, 20));

        OpenInvoiceLine line = Assert.Single(open);
        Assert.Equal(70m, line.OpenBalance);     // 100 - 30
        Assert.Equal(20, line.DaysOverdue);      // 2026-04-20 minus 2026-03-31
    }

    [Fact]
    public void OpenInvoices_excludes_fully_paid_and_uses_zero_overdue_when_no_due_date()
    {
        Guid paid = Guid.NewGuid(), noDue = Guid.NewGuid();
        Invoice paidInv = IssuedInvoice(paid, "1001", new(2026, 3, 1), new(2026, 3, 31), 100m);
        Invoice noDueInv = IssuedInvoice(noDue, "1002", new(2026, 3, 1), null, 50m);
        Dictionary<Guid, decimal> applied = new() { [paid] = 100m };   // fully paid

        IReadOnlyList<OpenInvoiceLine> open = CustomerAccountBuilder.OpenInvoices([paidInv, noDueInv], applied, asOf: new(2026, 6, 1));

        OpenInvoiceLine line = Assert.Single(open);                   // paid one excluded
        Assert.Equal(noDue, line.InvoiceId);
        Assert.Equal(0, line.DaysOverdue);                            // null due date → 0
    }

    [Fact]
    public void Aging_buckets_by_days_overdue_and_sums_to_ar_balance()
    {
        List<OpenInvoiceLine> lines =
        [
            new(Guid.NewGuid(), "a", new(2026, 1, 1), null, 100m, 0),     // Current
            new(Guid.NewGuid(), "b", new(2026, 1, 1), null, 200m, 15),    // 1-30
            new(Guid.NewGuid(), "c", new(2026, 1, 1), null, 300m, 45),    // 31-60
            new(Guid.NewGuid(), "d", new(2026, 1, 1), null, 400m, 75),    // 61-90
            new(Guid.NewGuid(), "e", new(2026, 1, 1), null, 500m, 120),   // 90+
        ];

        AgingBuckets aging = CustomerAccountBuilder.Aging(lines);

        Assert.Equal(100m, aging.Current);
        Assert.Equal(200m, aging.D1To30);
        Assert.Equal(300m, aging.D31To60);
        Assert.Equal(400m, aging.D61To90);
        Assert.Equal(500m, aging.D90Plus);
        Assert.Equal(1500m, aging.Current + aging.D1To30 + aging.D31To60 + aging.D61To90 + aging.D90Plus);
    }

    [Fact]
    public void Statement_orders_by_date_charges_first_with_running_balance()
    {
        Guid i1 = Guid.NewGuid(), i2 = Guid.NewGuid();
        Invoice inv1 = IssuedInvoice(i1, "1001", new(2026, 3, 1), null, 1000m);
        Invoice inv2 = IssuedInvoice(i2, "1002", new(2026, 3, 25), null, 1500m);
        List<Payment> payments = [new() { Id = Guid.NewGuid(), CustomerId = Guid.NewGuid(), Date = new(2026, 3, 15), Amount = 400m, Allocations = [new Allocation(i1, 400m)] }];
        List<CreditNote> notes = [new() { Id = Guid.NewGuid(), CustomerId = Guid.NewGuid(), Date = new(2026, 3, 20), Allocations = [new Allocation(i1, 200m)] }];

        IReadOnlyList<StatementLine> lines = CustomerAccountBuilder.Statement([inv1, inv2], payments, notes, [], []);

        Assert.Equal(4, lines.Count);
        Assert.Equal(1000m, lines[0].Charge); Assert.Equal(1000m, lines[0].Balance);   // 3/1 invoice
        Assert.Equal(400m, lines[1].Payment); Assert.Equal(600m, lines[1].Balance);    // 3/15 payment
        Assert.Equal(200m, lines[2].Payment); Assert.Equal(400m, lines[2].Balance);    // 3/20 credit note
        Assert.Equal(1500m, lines[3].Charge); Assert.Equal(1900m, lines[3].Balance);   // 3/25 invoice
    }

    [Fact]
    public void CreditActivity_signs_and_runs_to_final_balance()
    {
        List<Payment> payments = [new() { Id = Guid.NewGuid(), CustomerId = Guid.NewGuid(), Date = new(2026, 3, 2), Amount = 150m, Allocations = [new Allocation(Guid.NewGuid(), 50m)] }]; // 100 unapplied
        List<CreditApplication> apps = [new() { Id = Guid.NewGuid(), CustomerId = Guid.NewGuid(), Date = new(2026, 3, 10), Allocations = [new Allocation(Guid.NewGuid(), 30m)] }];
        List<Refund> refunds = [new() { Id = Guid.NewGuid(), CustomerId = Guid.NewGuid(), Date = new(2026, 3, 20), Amount = 20m }];

        IReadOnlyList<CreditActivityLine> lines = CustomerAccountBuilder.CreditActivity(payments, apps, refunds);

        Assert.Equal(3, lines.Count);
        Assert.Equal(100m, lines[0].Amount); Assert.Equal(100m, lines[0].CreditBalance);   // overpayment +100
        Assert.Equal(-30m, lines[1].Amount); Assert.Equal(70m, lines[1].CreditBalance);    // applied -30
        Assert.Equal(-20m, lines[2].Amount); Assert.Equal(50m, lines[2].CreditBalance);    // refund -20
    }

    [Fact]
    public void Statement_orders_same_date_charges_before_settlements()
    {
        Guid inv = Guid.NewGuid();
        Invoice invoice = IssuedInvoice(inv, "1001", new(2026, 3, 1), null, 1000m);
        List<Payment> payments = [new() { Id = Guid.NewGuid(), CustomerId = Guid.NewGuid(), Date = new(2026, 3, 1), Amount = 400m, Allocations = [new Allocation(inv, 400m)] }];

        IReadOnlyList<StatementLine> lines = CustomerAccountBuilder.Statement([invoice], payments, [], [], []);

        Assert.Equal(2, lines.Count);
        Assert.Equal(1000m, lines[0].Charge);    // Charge comes first on 3/1
        Assert.Equal(1000m, lines[0].Balance);   // 0 + 1000
        Assert.Equal(400m, lines[1].Payment);    // Payment comes second on same 3/1
        Assert.Equal(600m, lines[1].Balance);    // 1000 - 400
    }

    [Fact]
    public void Aging_buckets_exact_fencepost_boundaries()
    {
        List<OpenInvoiceLine> lines =
        [
            new(Guid.NewGuid(), "current", new(2026, 1, 1), null, 100m, 0),      // Current: 0
            new(Guid.NewGuid(), "d1to30_30", new(2026, 1, 1), null, 50m, 30),    // D1To30: exactly 30
            new(Guid.NewGuid(), "d31to60_31", new(2026, 1, 1), null, 75m, 31),   // D31To60: exactly 31
            new(Guid.NewGuid(), "d31to60_60", new(2026, 1, 1), null, 60m, 60),   // D31To60: exactly 60
            new(Guid.NewGuid(), "d61to90_61", new(2026, 1, 1), null, 85m, 61),   // D61To90: exactly 61
            new(Guid.NewGuid(), "d61to90_90", new(2026, 1, 1), null, 90m, 90),   // D61To90: exactly 90
            new(Guid.NewGuid(), "d90plus_91", new(2026, 1, 1), null, 110m, 91),  // D90Plus: exactly 91
        ];

        AgingBuckets aging = CustomerAccountBuilder.Aging(lines);

        Assert.Equal(100m, aging.Current);      // 0
        Assert.Equal(50m, aging.D1To30);        // 30
        Assert.Equal(135m, aging.D31To60);      // 31 + 60
        Assert.Equal(175m, aging.D61To90);      // 61 + 90
        Assert.Equal(110m, aging.D90Plus);      // 91
    }

    [Fact]
    public void AppliedByInvoice_excludes_voided_credit_applications_and_write_offs()
    {
        Guid inv = Guid.NewGuid();
        List<CreditApplication> creditApps =
        [
            new() { Id = Guid.NewGuid(), CustomerId = Guid.NewGuid(), Date = new(2026, 3, 1), Allocations = [new Allocation(inv, 50m)] },
            new() { Id = Guid.NewGuid(), CustomerId = Guid.NewGuid(), Date = new(2026, 3, 2), Allocations = [new Allocation(inv, 30m)], Voided = true },
        ];
        List<WriteOff> writeOffs =
        [
            new() { Id = Guid.NewGuid(), CustomerId = Guid.NewGuid(), Date = new(2026, 3, 3), Allocations = [new Allocation(inv, 25m)] },
            new() { Id = Guid.NewGuid(), CustomerId = Guid.NewGuid(), Date = new(2026, 3, 4), Allocations = [new Allocation(inv, 15m)], Voided = true },
        ];

        Dictionary<Guid, decimal> applied = CustomerAccountBuilder.AppliedByInvoice([], creditApps, writeOffs, []);

        Assert.Equal(75m, applied[inv]);  // 50 (credit app) + 25 (write-off); voided 30 + 15 excluded
    }

    [Fact]
    public void OpenInvoices_orders_same_date_by_number()
    {
        Invoice inv1002 = IssuedInvoice(Guid.NewGuid(), "1002", new(2026, 3, 1), null, 100m);
        Invoice inv1001 = IssuedInvoice(Guid.NewGuid(), "1001", new(2026, 3, 1), null, 100m);
        // fed in reversed number order; same issue date
        IReadOnlyList<OpenInvoiceLine> open =
            CustomerAccountBuilder.OpenInvoices([inv1002, inv1001], new Dictionary<Guid, decimal>(), asOf: new(2026, 3, 1));

        Assert.Equal(["1001", "1002"], open.Select(l => l.Number));
    }

    [Fact]
    public void CreditActivity_orders_same_date_deterministically_by_type_then_id()
    {
        DateOnly d = new(2026, 3, 5);
        Guid pA = new("00000000-0000-0000-0000-000000000001");
        Guid pB = new("00000000-0000-0000-0000-000000000002");
        // Two same-date overpayments fed high-Id first (Id tiebreak) + a same-date credit application (type Order after overpayments).
        List<Payment> payments =
        [
            new() { Id = pB, CustomerId = Guid.NewGuid(), Date = d, Amount = 20m, Allocations = [] }, // unapplied 20
            new() { Id = pA, CustomerId = Guid.NewGuid(), Date = d, Amount = 10m, Allocations = [] }, // unapplied 10
        ];
        List<CreditApplication> apps =
            [new() { Id = Guid.NewGuid(), CustomerId = Guid.NewGuid(), Date = d, Allocations = [new Allocation(Guid.NewGuid(), 5m)] }];

        IReadOnlyList<CreditActivityLine> lines = CustomerAccountBuilder.CreditActivity(payments, apps, []);

        // Overpayments first (Order 0), ordered by Id (pA=…01 before pB=…02) → 10 then 20; then Credit applied (Order 1) → -5.
        Assert.Equal([10m, 20m, -5m], lines.Select(l => l.Amount));
        Assert.Equal(["Overpayment", "Overpayment", "Credit applied"], lines.Select(l => l.Type));
    }
}
