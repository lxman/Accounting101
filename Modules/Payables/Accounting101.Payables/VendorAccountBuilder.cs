using Accounting101.Settlement;

namespace Accounting101.Payables;

/// <summary>Pure folds that turn a vendor's stored documents into the account view's parts. Every fold
/// ignores voided documents and is deterministic given its inputs (aging takes an explicit asOf).
/// Mirror of the receivables CustomerAccountBuilder, minus AR-only document types.</summary>
public static class VendorAccountBuilder
{
    public static Dictionary<Guid, decimal> AppliedByBill(
        IReadOnlyList<BillPayment> payments, IReadOnlyList<VendorCreditApplication> creditApps)
    {
        Dictionary<Guid, decimal> applied = new();
        void Add(IEnumerable<Allocation> allocs)
        {
            foreach (Allocation a in allocs) applied[a.TargetId] = applied.GetValueOrDefault(a.TargetId) + a.Amount;
        }
        Add(payments.Where(p => !p.Voided).SelectMany(p => p.Allocations));
        Add(creditApps.Where(c => !c.Voided).SelectMany(c => c.Allocations));
        return applied;
    }

    public static IReadOnlyList<OpenBillLine> OpenBills(
        IReadOnlyList<Bill> bills, IReadOnlyDictionary<Guid, decimal> applied, DateOnly asOf) =>
        bills.Where(b => b.Status == BillStatus.Entered)
            .Select(b =>
            {
                decimal open = Settlement.Settlement.OpenBalance(b.Total, applied.GetValueOrDefault(b.Id));
                int overdue = b.DueDate is { } due ? Math.Max(0, asOf.DayNumber - due.DayNumber) : 0;
                return new OpenBillLine(b.Id, b.Number, b.BillDate, b.DueDate, open, overdue);
            })
            .Where(l => l.OpenBalance > 0m)
            .OrderBy(l => l.BillDate).ToList();

    public static AgingBuckets Aging(IReadOnlyList<OpenBillLine> openBills)
    {
        decimal cur = 0, b1 = 0, b2 = 0, b3 = 0, b4 = 0;
        foreach (OpenBillLine l in openBills)
        {
            if (l.DaysOverdue <= 0) cur += l.OpenBalance;
            else if (l.DaysOverdue <= 30) b1 += l.OpenBalance;
            else if (l.DaysOverdue <= 60) b2 += l.OpenBalance;
            else if (l.DaysOverdue <= 90) b3 += l.OpenBalance;
            else b4 += l.OpenBalance;
        }
        return new AgingBuckets(cur, b1, b2, b3, b4);
    }

    public static decimal ApBalance(IReadOnlyList<OpenBillLine> openBills) => openBills.Sum(l => l.OpenBalance);

    public static IReadOnlyList<StatementLine> Statement(
        IReadOnlyList<Bill> bills, IReadOnlyList<BillPayment> payments, IReadOnlyList<VendorCreditApplication> creditApps)
    {
        List<(DateOnly Date, int Order, string Type, string? Reference, decimal Charge, decimal Payment)> raw = [];
        foreach (Bill b in bills.Where(b => b.Status == BillStatus.Entered))
            raw.Add((b.BillDate, 0, "Bill", b.Number, b.Total, 0m));
        foreach (BillPayment p in payments.Where(p => !p.Voided))
            raw.Add((p.Date, 1, "Payment", null, 0m, p.Allocations.Sum(a => a.Amount)));
        foreach (VendorCreditApplication c in creditApps.Where(c => !c.Voided))
            raw.Add((c.Date, 1, "Credit applied", null, 0m, c.Allocations.Sum(a => a.Amount)));

        decimal balance = 0m;
        return raw.OrderBy(r => r.Date).ThenBy(r => r.Order)
            .Select(r =>
            {
                balance += r.Charge - r.Payment;
                return new StatementLine(r.Date, r.Type, r.Reference, r.Charge, r.Payment, balance);
            }).ToList();
    }

    public static IReadOnlyList<CreditActivityLine> CreditActivity(
        IReadOnlyList<BillPayment> payments, IReadOnlyList<VendorCreditApplication> creditApps)
    {
        List<(DateOnly Date, string Type, string? Reference, decimal Amount)> raw = [];
        foreach (BillPayment p in payments.Where(p => !p.Voided && p.Unapplied > 0m))
            raw.Add((p.Date, "Overpayment", null, p.Unapplied));
        foreach (VendorCreditApplication c in creditApps.Where(c => !c.Voided))
            raw.Add((c.Date, "Credit applied", null, -c.Applied));

        decimal balance = 0m;
        return raw.OrderBy(r => r.Date)
            .Select(r =>
            {
                balance += r.Amount;
                return new CreditActivityLine(r.Date, r.Type, r.Reference, r.Amount, balance);
            }).ToList();
    }
}
