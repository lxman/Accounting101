namespace Accounting101.Receivables;

/// <summary>Pure folds that turn a customer's stored documents into the account view's parts. Every fold
/// ignores voided documents and is deterministic given its inputs (aging takes an explicit asOf).</summary>
public static class CustomerAccountBuilder
{
    /// <summary>Issued invoices with a positive open balance, each with days overdue (0 when not yet due
    /// or no due date), oldest issue first.</summary>
    public static IReadOnlyList<OpenInvoiceLine> OpenInvoices(
        IReadOnlyList<Invoice> invoices, IReadOnlyDictionary<Guid, decimal> applied, DateOnly asOf) =>
        invoices.Where(i => i.Status == InvoiceStatus.Issued)
            .Select(i =>
            {
                decimal open = Settlement.Settlement.OpenBalance(i.Total, applied.GetValueOrDefault(i.Id));
                int overdue = i.DueDate is { } due ? Math.Max(0, asOf.DayNumber - due.DayNumber) : 0;
                return new OpenInvoiceLine(i.Id, i.Number, i.IssueDate, i.DueDate, open, overdue);
            })
            .Where(l => l.OpenBalance > 0m)
            .OrderBy(l => l.IssueDate).ThenBy(l => l.Number).ToList();

    public static AgingBuckets Aging(IReadOnlyList<OpenInvoiceLine> openInvoices)
    {
        decimal cur = 0, b1 = 0, b2 = 0, b3 = 0, b4 = 0;
        foreach (OpenInvoiceLine l in openInvoices)
        {
            if (l.DaysOverdue <= 0) cur += l.OpenBalance;
            else if (l.DaysOverdue <= 30) b1 += l.OpenBalance;
            else if (l.DaysOverdue <= 60) b2 += l.OpenBalance;
            else if (l.DaysOverdue <= 90) b3 += l.OpenBalance;
            else b4 += l.OpenBalance;
        }
        return new AgingBuckets(cur, b1, b2, b3, b4);
    }

    public static decimal ArBalance(IReadOnlyList<OpenInvoiceLine> openInvoices) => openInvoices.Sum(l => l.OpenBalance);

    /// <summary>The AR statement: a charge per issued invoice, a settlement line per non-voided payment /
    /// credit-note / write-off / credit-application (amount = its AR relief), oldest first with charges
    /// before settlements on the same date, carrying a running AR balance. <paramref name="reliefByDocument"/>
    /// maps each settlement document's Id to its total AR relief, folded from the ledger (the module stores
    /// no allocation array to sum directly).</summary>
    public static IReadOnlyList<StatementLine> Statement(
        IReadOnlyList<Invoice> invoices, IReadOnlyList<Payment> payments,
        IReadOnlyList<CreditNote> creditNotes, IReadOnlyList<WriteOff> writeOffs,
        IReadOnlyList<CreditApplication> creditApps, IReadOnlyDictionary<Guid, decimal> reliefByDocument)
    {
        List<(DateOnly Date, int Order, Guid Id, string Kind, string Type, string? Reference, decimal Charge, decimal Payment)> raw = [];
        foreach (Invoice i in invoices.Where(i => i.Status == InvoiceStatus.Issued))
            raw.Add((i.IssueDate, 0, i.Id, "invoice", "Invoice", i.Number, i.Total, 0m));
        // Settlement.Payment column = the document's AR relief (total cash/credit applied to invoices).
        // The running balance subtracts each settlement's relief in full, while ArBalance floors each
        // invoice's open balance at 0 via Settlement.OpenBalance; these agree as long as allocations never
        // over-apply an invoice (enforced upstream by allocation validation).
        foreach (Payment p in payments.Where(p => !p.Voided))
            raw.Add((p.Date, 1, p.Id, "payment", "Payment", null, 0m, reliefByDocument.GetValueOrDefault(p.Id)));
        foreach (CreditNote n in creditNotes.Where(n => !n.Voided))
            raw.Add((n.Date, 1, n.Id, "credit-note", "Credit note", n.Memo, 0m, reliefByDocument.GetValueOrDefault(n.Id)));
        foreach (WriteOff w in writeOffs.Where(w => !w.Voided))
            raw.Add((w.Date, 1, w.Id, "write-off", "Write-off", w.Memo, 0m, reliefByDocument.GetValueOrDefault(w.Id)));
        foreach (CreditApplication c in creditApps.Where(c => !c.Voided))
            raw.Add((c.Date, 1, c.Id, "credit-application", "Credit applied", null, 0m, reliefByDocument.GetValueOrDefault(c.Id)));

        decimal balance = 0m;
        return raw.OrderBy(r => r.Date).ThenBy(r => r.Order)
            .Select(r =>
            {
                balance += r.Charge - r.Payment;
                return new StatementLine(r.Date, r.Type, r.Reference, r.Charge, r.Payment, balance, r.Id, r.Kind);
            }).ToList();
    }

    /// <summary>The credit ledger: overpayment remainders (+), credit-applications (−), refunds (−), oldest
    /// first, with a running credit balance that ends at the customer's unapplied credit.
    /// <paramref name="reliefByDocument"/> maps each payment/credit-application's Id to its total AR relief,
    /// folded from the ledger — a payment's unapplied remainder is its Amount minus that relief, and a
    /// credit-application's applied amount IS that relief.</summary>
    public static IReadOnlyList<CreditActivityLine> CreditActivity(
        IReadOnlyList<Payment> payments, IReadOnlyList<CreditApplication> creditApps, IReadOnlyList<Refund> refunds,
        IReadOnlyDictionary<Guid, decimal> reliefByDocument)
    {
        List<(DateOnly Date, int Order, Guid Id, string Kind, string Type, string? Reference, decimal Amount)> raw = [];
        foreach (Payment p in payments.Where(p => !p.Voided))
        {
            decimal unapplied = p.Amount - reliefByDocument.GetValueOrDefault(p.Id);
            if (unapplied > 0m) raw.Add((p.Date, 0, p.Id, "payment", "Overpayment", null, unapplied));
        }
        foreach (CreditApplication c in creditApps.Where(c => !c.Voided))
            raw.Add((c.Date, 1, c.Id, "credit-application", "Credit applied", null, -reliefByDocument.GetValueOrDefault(c.Id)));
        foreach (Refund r in refunds.Where(r => !r.Voided))
            raw.Add((r.Date, 2, r.Id, "refund", "Refund", r.Memo, -r.Amount));

        decimal balance = 0m;
        return raw.OrderBy(r => r.Date).ThenBy(r => r.Order).ThenBy(r => r.Id)
            .Select(r =>
            {
                balance += r.Amount;
                return new CreditActivityLine(r.Date, r.Type, r.Reference, r.Amount, balance, r.Id, r.Kind);
            }).ToList();
    }
}
