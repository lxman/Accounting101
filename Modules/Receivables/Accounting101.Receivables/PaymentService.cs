using Accounting101.Ledger.Contracts;
using Accounting101.Settlement;

namespace Accounting101.Receivables;

/// <summary>The cash-application lifecycle: record a payment (allocate across invoices, hold over-payment as
/// customer credit), apply existing credit, and void. Each document posts one balanced entry that lands
/// PendingApproval — approval is the client's normal maker-checker flow. Open balances and credit are
/// derived from stored allocations, never stored.</summary>
public sealed class PaymentService(
    IPaymentStore payments, IInvoiceStore invoices, IPaymentAccountsProvider accounts, ILedgerClient ledger)
{
    public async Task<Payment> RecordPaymentAsync(Guid clientId, PaymentBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (body.Amount <= 0m)
            throw new InvalidOperationException("A payment amount must be greater than zero.");
        if (body.Allocations.Any(a => a.Amount <= 0m))
            throw new InvalidOperationException("Every allocation amount must be greater than zero.");
        decimal allocated = body.Allocations.Sum(a => a.Amount);
        if (allocated > body.Amount)
            throw new InvalidOperationException("Allocations cannot exceed the payment amount.");

        await ValidateAllocationsAsync(clientId, body.CustomerId, body.Allocations, ct);

        Payment recorded = await payments.RecordPaymentAsync(clientId, body, ct);
        PaymentPostingAccounts posting = await accounts.GetAsync(clientId, ct);
        PostEntryRequest entry = PaymentPosting.ComposePayment(recorded.Id, body, posting);
        await ledger.PostAsync(clientId, entry, ct);
        return recorded;
    }

    /// <summary>Each allocation must target a live invoice of this customer and not exceed its current open balance.</summary>
    private async Task ValidateAllocationsAsync(Guid clientId, Guid customerId, IReadOnlyList<Allocation> allocations, CancellationToken ct)
    {
        foreach (Allocation a in allocations)
        {
            Invoice invoice = await invoices.GetAsync(clientId, a.TargetId, ct)
                ?? throw new InvalidOperationException($"Invoice {a.TargetId} does not exist.");
            if (invoice.Status != InvoiceStatus.Issued)
                throw new InvalidOperationException($"Invoice {a.TargetId} is {invoice.Status} — only issued invoices can be paid.");
            if (invoice.CustomerId != customerId)
                throw new InvalidOperationException($"Invoice {a.TargetId} belongs to a different customer.");

            decimal alreadyApplied = await AppliedToInvoiceAsync(clientId, customerId, a.TargetId, ct);
            if (alreadyApplied + a.Amount > invoice.Total)
                throw new InvalidOperationException($"Allocation to invoice {a.TargetId} exceeds its open balance.");
        }
    }

    public async Task<IReadOnlyList<InvoiceView>> ListInvoiceViewsAsync(Guid clientId, Guid customerId, SettlementFilter? filter, CancellationToken ct = default)
    {
        IReadOnlyList<Invoice> customerInvoices = await invoices.GetByCustomerAsync(clientId, customerId, ct);
        IReadOnlyList<Payment> ps = await payments.GetPaymentsByCustomerAsync(clientId, customerId, ct);
        IReadOnlyList<CreditApplication> cs = await payments.GetCreditApplicationsByCustomerAsync(clientId, customerId, ct);
        IReadOnlyList<WriteOff> ws = await payments.GetWriteOffsByCustomerAsync(clientId, customerId, ct);
        IReadOnlyList<CreditNote> ns = await payments.GetCreditNotesByCustomerAsync(clientId, customerId, ct);

        Dictionary<Guid, decimal> applied = new();
        foreach (Allocation a in ps.Where(p => !p.Voided).SelectMany(p => p.Allocations)
                     .Concat(cs.Where(c => !c.Voided).SelectMany(c => c.Allocations)))
            applied[a.TargetId] = applied.GetValueOrDefault(a.TargetId) + a.Amount;
        foreach (Allocation a in ws.Where(w => !w.Voided).SelectMany(w => w.Allocations)
                     .Concat(ns.Where(n => !n.Voided).SelectMany(n => n.Allocations)))
            applied[a.TargetId] = applied.GetValueOrDefault(a.TargetId) + a.Amount;

        IEnumerable<InvoiceView> views = customerInvoices
            .Where(inv => inv.Status == InvoiceStatus.Issued)
            .Select(inv =>
            {
                decimal ap = applied.GetValueOrDefault(inv.Id);
                return new InvoiceView(inv, Accounting101.Settlement.Settlement.OpenBalance(inv.Total, ap), Accounting101.Settlement.Settlement.Status(inv.Total, ap));
            });

        views = filter switch
        {
            SettlementFilter.Open => views.Where(v => v.SettlementStatus != SettlementStatus.Paid),
            SettlementFilter.Paid => views.Where(v => v.SettlementStatus == SettlementStatus.Paid),
            _ => views,
        };
        return views.ToList();
    }

    public async Task<InvoiceView?> GetInvoiceViewAsync(Guid clientId, Guid invoiceId, CancellationToken ct = default)
    {
        Invoice? invoice = await invoices.GetAsync(clientId, invoiceId, ct);
        if (invoice is null) return null;
        decimal applied = await AppliedToInvoiceAsync(clientId, invoice.CustomerId, invoiceId, ct);
        return new InvoiceView(invoice, Accounting101.Settlement.Settlement.OpenBalance(invoice.Total, applied), Accounting101.Settlement.Settlement.Status(invoice.Total, applied));
    }

    /// <summary>All payments recorded for a customer (including voided), newest-or-stored order. Read-only;
    /// powers the UI's applied-payments view.</summary>
    public Task<IReadOnlyList<Payment>> GetPaymentsByCustomerAsync(Guid clientId, Guid customerId, CancellationToken ct = default) =>
        payments.GetPaymentsByCustomerAsync(clientId, customerId, ct);

    /// <summary>The customer's refunds (cash returned against credit), date-descending. Read-only; powers the
    /// Refunds list. Includes voided refunds (greyed in the UI).</summary>
    public async Task<IReadOnlyList<Refund>> GetRefundsByCustomerAsync(
        Guid clientId, Guid customerId, CancellationToken ct = default)
    {
        IReadOnlyList<Refund> refunds = await payments.GetRefundsByCustomerAsync(clientId, customerId, ct);
        return refunds.OrderByDescending(r => r.Date).ToList();
    }

    /// <summary>The customer's allocation-based dispositions — credit notes, write-offs, and credit
    /// applications — as one date-descending list. Read-only; powers the Credits list. Memo comes from the
    /// stored note/write-off; credit applications carry none.</summary>
    public async Task<IReadOnlyList<CreditDocument>> GetCreditsByCustomerAsync(
        Guid clientId, Guid customerId, CancellationToken ct = default)
    {
        IReadOnlyList<CreditNote> notes = await payments.GetCreditNotesByCustomerAsync(clientId, customerId, ct);
        IReadOnlyList<WriteOff> writeOffs = await payments.GetWriteOffsByCustomerAsync(clientId, customerId, ct);
        IReadOnlyList<CreditApplication> apps = await payments.GetCreditApplicationsByCustomerAsync(clientId, customerId, ct);

        IEnumerable<CreditDocument> all =
            notes.Select(n => new CreditDocument("credit-note", n.Id, n.CustomerId, n.Date, n.Total, n.Memo, n.Allocations, n.Voided))
            .Concat(writeOffs.Select(w => new CreditDocument("write-off", w.Id, w.CustomerId, w.Date, w.Total, w.Memo, w.Allocations, w.Voided)))
            .Concat(apps.Select(a => new CreditDocument("credit-application", a.Id, a.CustomerId, a.Date, a.Applied, null, a.Allocations, a.Voided)));

        return all.OrderByDescending(c => c.Date).ToList();
    }

    /// <summary>Unapplied customer credit = non-voided payment remainders minus non-voided credit applications minus non-voided refunds.</summary>
    public async Task<decimal> GetCustomerCreditBalanceAsync(Guid clientId, Guid customerId, CancellationToken ct = default)
    {
        IReadOnlyList<Payment> ps = await payments.GetPaymentsByCustomerAsync(clientId, customerId, ct);
        IReadOnlyList<CreditApplication> cs = await payments.GetCreditApplicationsByCustomerAsync(clientId, customerId, ct);
        IReadOnlyList<Refund> rs = await payments.GetRefundsByCustomerAsync(clientId, customerId, ct);
        decimal created = ps.Where(p => !p.Voided).Sum(p => p.Unapplied);
        decimal spent = cs.Where(c => !c.Voided).Sum(c => c.Applied);
        decimal refunded = rs.Where(r => !r.Voided).Sum(r => r.Amount);
        return created - spent - refunded;
    }

    public async Task<CreditApplication> RecordCreditApplicationAsync(Guid clientId, CreditApplicationBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (body.Allocations.Count == 0 || body.Allocations.Any(a => a.Amount <= 0m))
            throw new InvalidOperationException("A credit application needs positive allocations.");

        decimal applying = body.Allocations.Sum(a => a.Amount);
        decimal available = await GetCustomerCreditBalanceAsync(clientId, body.CustomerId, ct);
        if (applying > available)
            throw new InvalidOperationException($"Credit application of {applying} exceeds available credit {available}.");

        await ValidateAllocationsAsync(clientId, body.CustomerId, body.Allocations, ct);

        CreditApplication recorded = await payments.RecordCreditApplicationAsync(clientId, body, ct);
        PaymentPostingAccounts posting = await accounts.GetAsync(clientId, ct);
        PostEntryRequest entry = PaymentPosting.ComposeCreditApplication(recorded.Id, body, posting);
        await ledger.PostAsync(clientId, entry, ct);
        return recorded;
    }

    public async Task<Payment> VoidPaymentAsync(Guid clientId, Guid paymentId, string? reason = null, CancellationToken ct = default)
    {
        Payment payment = await payments.GetPaymentAsync(clientId, paymentId, ct)
            ?? throw new InvalidOperationException($"Payment {paymentId} not found.");
        if (payment.Voided)
            throw new InvalidOperationException($"Payment {paymentId} is already voided.");

        // A void reverses the whole payment, including the overpayment that landed as customer credit. If that
        // credit has since been applied or refunded, removing it would drive the credit balance negative (a
        // debit balance on a liability) — a corrupt state. Refuse; the consuming application/refund must be
        // reversed first.
        if (payment.Unapplied > 0m)
        {
            decimal creditBalance = await GetCustomerCreditBalanceAsync(clientId, payment.CustomerId, ct);
            if (creditBalance - payment.Unapplied < 0m)
                throw new InvalidOperationException(
                    $"Cannot void payment {paymentId}: its overpayment credit ({payment.Unapplied:C}) has already " +
                    $"been applied or refunded (available credit is only {creditBalance:C}). Reverse the credit " +
                    $"application(s)/refund(s) first, then void this payment.");
        }

        await VoidLedgerEntryAsync(clientId, paymentId, payment.Date, "payment", reason, ct);

        await payments.VoidAsync(clientId, paymentId, ct);
        return (await payments.GetPaymentAsync(clientId, paymentId, ct))!;
    }

    public async Task<WriteOff> RecordWriteOffAsync(Guid clientId, WriteOffBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (body.Allocations.Count == 0 || body.Allocations.Any(a => a.Amount <= 0m))
            throw new InvalidOperationException("A write-off needs positive allocations.");
        await ValidateAllocationsAsync(clientId, body.CustomerId, body.Allocations, ct);
        WriteOff recorded = await payments.RecordWriteOffAsync(clientId, body, ct);
        PaymentPostingAccounts posting = await accounts.GetAsync(clientId, ct);
        await ledger.PostAsync(clientId, PaymentPosting.ComposeWriteOff(recorded.Id, body, posting), ct);
        return recorded;
    }

    public async Task<WriteOff> VoidWriteOffAsync(Guid clientId, Guid writeOffId, string? reason = null, CancellationToken ct = default)
    {
        WriteOff writeOff = await payments.GetWriteOffAsync(clientId, writeOffId, ct)
            ?? throw new InvalidOperationException($"Write-off {writeOffId} not found.");
        if (writeOff.Voided) throw new InvalidOperationException($"Write-off {writeOffId} is already voided.");

        await VoidLedgerEntryAsync(clientId, writeOffId, writeOff.Date, "write-off", reason, ct);

        await payments.VoidWriteOffAsync(clientId, writeOffId, ct);
        return (await payments.GetWriteOffAsync(clientId, writeOffId, ct))!;
    }

    public async Task<CreditNote> RecordCreditNoteAsync(Guid clientId, CreditNoteBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (body.Allocations.Count == 0 || body.Allocations.Any(a => a.Amount <= 0m))
            throw new InvalidOperationException("A credit note needs positive allocations.");
        await ValidateAllocationsAsync(clientId, body.CustomerId, body.Allocations, ct);
        CreditNote recorded = await payments.RecordCreditNoteAsync(clientId, body, ct);
        PaymentPostingAccounts posting = await accounts.GetAsync(clientId, ct);
        await ledger.PostAsync(clientId, PaymentPosting.ComposeCreditNote(recorded.Id, body, posting), ct);
        return recorded;
    }

    public async Task<CreditNote> VoidCreditNoteAsync(Guid clientId, Guid creditNoteId, string? reason = null, CancellationToken ct = default)
    {
        CreditNote creditNote = await payments.GetCreditNoteAsync(clientId, creditNoteId, ct)
            ?? throw new InvalidOperationException($"Credit note {creditNoteId} not found.");
        if (creditNote.Voided) throw new InvalidOperationException($"Credit note {creditNoteId} is already voided.");

        await VoidLedgerEntryAsync(clientId, creditNoteId, creditNote.Date, "credit note", reason, ct);

        await payments.VoidCreditNoteAsync(clientId, creditNoteId, ct);
        return (await payments.GetCreditNoteAsync(clientId, creditNoteId, ct))!;
    }

    public async Task<Refund> RecordRefundAsync(Guid clientId, RefundBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (body.Amount <= 0m) throw new InvalidOperationException("A refund amount must be greater than zero.");
        decimal available = await GetCustomerCreditBalanceAsync(clientId, body.CustomerId, ct);
        if (body.Amount > available)
            throw new InvalidOperationException($"Refund of {body.Amount} exceeds available credit {available}.");
        Refund recorded = await payments.RecordRefundAsync(clientId, body, ct);
        PaymentPostingAccounts posting = await accounts.GetAsync(clientId, ct);
        await ledger.PostAsync(clientId, PaymentPosting.ComposeRefund(recorded.Id, body, posting), ct);
        return recorded;
    }

    public async Task<Refund> VoidRefundAsync(Guid clientId, Guid refundId, string? reason = null, CancellationToken ct = default)
    {
        Refund refund = await payments.GetRefundAsync(clientId, refundId, ct)
            ?? throw new InvalidOperationException($"Refund {refundId} not found.");
        if (refund.Voided) throw new InvalidOperationException($"Refund {refundId} is already voided.");

        await VoidLedgerEntryAsync(clientId, refundId, refund.Date, "refund", reason, ct);

        await payments.VoidRefundAsync(clientId, refundId, ct);
        return (await payments.GetRefundAsync(clientId, refundId, ct))!;
    }

    /// <summary>
    /// Shared ledger-entry transition for all void operations: find the single Active, non-reversal entry
    /// for <paramref name="sourceRef"/> and either reverse it (if already Posted) or void it (if pending).
    /// </summary>
    private async Task VoidLedgerEntryAsync(
        Guid clientId, Guid sourceRef, DateOnly date, string label, string? reason, CancellationToken ct)
    {
        IReadOnlyList<EntryResponse> spawned = await ledger.GetEntriesBySourceRefAsync(clientId, sourceRef, ct);
        EntryResponse entry = spawned.FirstOrDefault(e => e is { Status: "Active", ReversalOf: null })
            ?? throw new InvalidOperationException($"No entry found for {label} {sourceRef} to void.");

        if (entry.Posting == "Posted")
            await ledger.ReverseAsync(clientId, entry.Id, new ReverseRequest(date, reason ?? $"Voided {label} {sourceRef}"), ct);
        else
            await ledger.VoidAsync(clientId, entry.Id, new VoidRequest(reason ?? $"Voided {label} {sourceRef}"), ct);
    }

    /// <summary>Total non-voided allocations (payments + credit applications + write-offs + credit notes) applied to one invoice.</summary>
    private async Task<decimal> AppliedToInvoiceAsync(Guid clientId, Guid customerId, Guid invoiceId, CancellationToken ct)
    {
        IReadOnlyList<Payment> ps = await payments.GetPaymentsByCustomerAsync(clientId, customerId, ct);
        IReadOnlyList<CreditApplication> cs = await payments.GetCreditApplicationsByCustomerAsync(clientId, customerId, ct);
        IReadOnlyList<WriteOff> ws = await payments.GetWriteOffsByCustomerAsync(clientId, customerId, ct);
        IReadOnlyList<CreditNote> ns = await payments.GetCreditNotesByCustomerAsync(clientId, customerId, ct);
        decimal fromPayments = ps.Where(p => !p.Voided).SelectMany(p => p.Allocations).Where(x => x.TargetId == invoiceId).Sum(x => x.Amount);
        decimal fromCredits = cs.Where(c => !c.Voided).SelectMany(c => c.Allocations).Where(x => x.TargetId == invoiceId).Sum(x => x.Amount);
        decimal fromWriteOffs = ws.Where(w => !w.Voided).SelectMany(w => w.Allocations).Where(x => x.TargetId == invoiceId).Sum(x => x.Amount);
        decimal fromCreditNotes = ns.Where(n => !n.Voided).SelectMany(n => n.Allocations).Where(x => x.TargetId == invoiceId).Sum(x => x.Amount);
        return fromPayments + fromCredits + fromWriteOffs + fromCreditNotes;
    }
}
