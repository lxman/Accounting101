using Accounting101.Settlement;

namespace Accounting101.Payables;

/// <summary>Pure folds that turn a vendor's stored documents into the account view's parts. Every fold
/// ignores voided documents and is deterministic given its inputs (aging takes an explicit asOf).
/// Mirror of the receivables CustomerAccountBuilder, minus AR-only document types.</summary>
public static class VendorAccountBuilder
{
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
            .OrderBy(l => l.BillDate).ThenBy(l => l.Number).ToList();

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
        IReadOnlyList<Bill> bills, IReadOnlyList<BillPayment> payments, IReadOnlyList<VendorCreditApplication> creditApps,
        IReadOnlyDictionary<Guid, decimal> reliefByDocument)
    {
        List<(DateOnly Date, int Order, Guid Id, string Kind, string Type, string? Reference, decimal Charge, decimal Payment)> raw = [];
        foreach (Bill b in bills.Where(b => b.Status == BillStatus.Entered))
            raw.Add((b.BillDate, 0, b.Id, "bill", "Bill", b.Number, b.Total, 0m));
        foreach (BillPayment p in payments.Where(p => !p.Voided))
            raw.Add((p.Date, 1, p.Id, "payment", "Payment", null, 0m, reliefByDocument.GetValueOrDefault(p.Id)));
        foreach (VendorCreditApplication c in creditApps.Where(c => !c.Voided))
            raw.Add((c.Date, 1, c.Id, "credit-application", "Credit applied", null, 0m, reliefByDocument.GetValueOrDefault(c.Id)));

        decimal balance = 0m;
        return raw.OrderBy(r => r.Date).ThenBy(r => r.Order)
            .Select(r =>
            {
                balance += r.Charge - r.Payment;
                return new StatementLine(r.Date, r.Type, r.Reference, r.Charge, r.Payment, balance, r.Id, r.Kind);
            }).ToList();
    }

    public static IReadOnlyList<CreditActivityLine> CreditActivity(
        IReadOnlyList<BillPayment> payments, IReadOnlyList<VendorCreditApplication> creditApps,
        IReadOnlyDictionary<Guid, decimal> reliefByDocument)
    {
        List<(DateOnly Date, int Order, Guid Id, string Kind, string Type, string? Reference, decimal Amount)> raw = [];
        foreach (BillPayment p in payments.Where(p => !p.Voided))
        {
            decimal overpayment = p.Amount - reliefByDocument.GetValueOrDefault(p.Id);
            if (overpayment > 0m) raw.Add((p.Date, 0, p.Id, "payment", "Overpayment", null, overpayment));
        }
        foreach (VendorCreditApplication c in creditApps.Where(c => !c.Voided))
            raw.Add((c.Date, 1, c.Id, "credit-application", "Credit applied", null, -reliefByDocument.GetValueOrDefault(c.Id)));

        decimal balance = 0m;
        return raw.OrderBy(r => r.Date).ThenBy(r => r.Order).ThenBy(r => r.Id)
            .Select(r =>
            {
                balance += r.Amount;
                return new CreditActivityLine(r.Date, r.Type, r.Reference, r.Amount, balance, r.Id, r.Kind);
            }).ToList();
    }
}
