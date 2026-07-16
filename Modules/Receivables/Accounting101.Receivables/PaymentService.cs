using Accounting101.Ledger.Contracts;
using Accounting101.Settlement;

namespace Accounting101.Receivables;

/// <summary>The cash-application lifecycle: record a payment (allocate across invoices, hold over-payment as
/// customer credit), apply existing credit, and void. Each document posts one balanced entry that lands
/// PendingApproval — approval is the client's normal maker-checker flow. The module stores no allocation
/// amounts at all; open balances and credit are folded from the ledger's Invoice/Customer-axis subledgers
/// on every read.</summary>
public sealed class PaymentService(
    IPaymentStore payments, IInvoiceStore invoices, IPaymentAccountsProvider accounts, ILedgerClient ledger)
{
    public async Task<Payment> RecordPaymentAsync(Guid clientId, PaymentCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Amount <= 0m)
            throw new InvalidOperationException("A payment amount must be greater than zero.");
        if (command.Allocations.Any(a => a.Amount <= 0m))
            throw new InvalidOperationException("Every allocation amount must be greater than zero.");
        decimal allocated = command.Allocations.Sum(a => a.Amount);
        if (allocated > command.Amount)
            throw new InvalidOperationException("Allocations cannot exceed the payment amount.");

        await ValidateAllocationsAsync(clientId, command.CustomerId, command.Allocations, ct);

        PaymentBody body = new(command.CustomerId, command.Date, command.Amount, command.Method);
        Payment recorded = await payments.RecordPaymentAsync(clientId, body, ct);
        PaymentPostingAccounts posting = await accounts.GetAsync(clientId, ct);
        PostEntryRequest entry = PaymentPosting.ComposePayment(recorded.Id, body, command.Allocations, posting);
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

            decimal alreadyApplied = await ReservedAgainstInvoiceAsync(clientId, invoice, ct);
            if (alreadyApplied + a.Amount > invoice.Total)
                throw new InvalidOperationException($"Allocation to invoice {a.TargetId} exceeds its open balance.");
        }
    }

    public async Task<IReadOnlyList<InvoiceView>> ListInvoiceViewsAsync(Guid clientId, Guid customerId, SettlementFilter? filter, CancellationToken ct = default)
    {
        IReadOnlyList<Invoice> customerInvoices = await invoices.GetByCustomerAsync(clientId, customerId, ct);

        PaymentPostingAccounts posting = await accounts.GetAsync(clientId, ct);
        IReadOnlyList<SubledgerLineResponse> arByInvoice =
            await ledger.GetSubledgerAsync(clientId, posting.ReceivableAccountId, "Invoice", null, ct);
        Dictionary<Guid, decimal> openByInvoice = arByInvoice.ToDictionary(l => l.DimensionValue, l => l.Balance);
        Dictionary<Guid, decimal> applied = customerInvoices.ToDictionary(
            i => i.Id, i => i.Total - openByInvoice.GetValueOrDefault(i.Id, i.Total));

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
        decimal applied = await AppliedToInvoiceAsync(clientId, invoice, ct);
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

    /// <summary>A single refund plus its posted journal entry id (for the detail screen's drill-in).
    /// The entry is the original posting sourced from the refund; null if none is found.</summary>
    public async Task<RefundView?> GetRefundViewAsync(Guid clientId, Guid refundId, CancellationToken ct = default)
    {
        Refund? refund = await payments.GetRefundAsync(clientId, refundId, ct);
        if (refund is null) return null;
        IReadOnlyList<EntryResponse> spawned = await ledger.GetEntriesBySourceRefAsync(clientId, refundId, ct);
        EntryResponse? posting = spawned.FirstOrDefault(e => e is { Status: "Active", ReversalOf: null });
        return new RefundView(refund, posting?.Id);
    }

    /// <summary>The customer's allocation-based dispositions — credit notes, write-offs, and credit
    /// applications — as one date-descending list. Read-only; powers the Credits list. Memo comes from the
    /// stored note/write-off; credit applications carry none. Amount is each document's AR relief, folded
    /// Posted-only from its ledger entry (the module stores no allocation array to sum directly) — a
    /// document's relief must not show before its own posting does.</summary>
    public async Task<IReadOnlyList<CreditDocument>> GetCreditsByCustomerAsync(
        Guid clientId, Guid customerId, CancellationToken ct = default)
    {
        IReadOnlyList<CreditNote> notes = await payments.GetCreditNotesByCustomerAsync(clientId, customerId, ct);
        IReadOnlyList<WriteOff> writeOffs = await payments.GetWriteOffsByCustomerAsync(clientId, customerId, ct);
        IReadOnlyList<CreditApplication> apps = await payments.GetCreditApplicationsByCustomerAsync(clientId, customerId, ct);
        PaymentPostingAccounts posting = await accounts.GetAsync(clientId, ct);

        List<CreditDocument> all = [];
        foreach (CreditNote n in notes)
            all.Add(new CreditDocument("credit-note", n.Id, n.CustomerId, n.Date,
                await SettlementRelief.ForSourceAsync(ledger, clientId, n.Id, posting.ReceivableAccountId, ct, postedOnly: true), n.Memo, n.Voided));
        foreach (WriteOff w in writeOffs)
            all.Add(new CreditDocument("write-off", w.Id, w.CustomerId, w.Date,
                await SettlementRelief.ForSourceAsync(ledger, clientId, w.Id, posting.ReceivableAccountId, ct, postedOnly: true), w.Memo, w.Voided));
        foreach (CreditApplication a in apps)
            all.Add(new CreditDocument("credit-application", a.Id, a.CustomerId, a.Date,
                await SettlementRelief.ForSourceAsync(ledger, clientId, a.Id, posting.ReceivableAccountId, ct, postedOnly: true), null, a.Voided));

        return all.OrderByDescending(c => c.Date).ToList();
    }

    /// <summary>Unapplied customer credit, folded from the ledger's Customer-axis Customer Credits
    /// subledger. Customer Credits is a liability (credit-normal); the ledger's debit-positive fold reads
    /// a positive available credit as NEGATIVE — negate it to present a positive balance.</summary>
    public async Task<decimal> GetCustomerCreditBalanceAsync(Guid clientId, Guid customerId, CancellationToken ct = default)
    {
        PaymentPostingAccounts posting = await accounts.GetAsync(clientId, ct);
        return (await ledger.GetSubledgerAsync(clientId, posting.CustomerCreditsAccountId, "Customer", null, ct))
            .Where(l => l.DimensionValue == customerId).Sum(l => -l.Balance);
    }

    public async Task<CreditApplication> RecordCreditApplicationAsync(Guid clientId, CreditApplicationCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Allocations.Count == 0 || command.Allocations.Any(a => a.Amount <= 0m))
            throw new InvalidOperationException("A credit application needs positive allocations.");

        decimal applying = command.Allocations.Sum(a => a.Amount);
        decimal available = await GetCustomerCreditBalanceAsync(clientId, command.CustomerId, ct);
        if (applying > available)
            throw new InvalidOperationException($"Credit application of {applying} exceeds available credit {available}.");

        await ValidateAllocationsAsync(clientId, command.CustomerId, command.Allocations, ct);

        CreditApplicationBody body = new(command.CustomerId, command.Date);
        CreditApplication recorded = await payments.RecordCreditApplicationAsync(clientId, body, ct);
        PaymentPostingAccounts posting = await accounts.GetAsync(clientId, ct);
        PostEntryRequest entry = PaymentPosting.ComposeCreditApplication(recorded.Id, body, command.Allocations, posting);
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
        // reversed first. Unapplied is folded from the payment's own entry (the module stores no allocation
        // array to compute it from directly).
        PaymentPostingAccounts postingForVoid = await accounts.GetAsync(clientId, ct);
        decimal allocated = await SettlementRelief.ForSourceAsync(
            ledger, clientId, paymentId, postingForVoid.ReceivableAccountId, ct, postedOnly: false);
        decimal unapplied = payment.Amount - allocated;
        if (unapplied > 0m)
        {
            decimal creditBalance = await GetCustomerCreditBalanceAsync(clientId, payment.CustomerId, ct);
            if (creditBalance - unapplied < 0m)
                throw new InvalidOperationException(
                    $"Cannot void payment {paymentId}: its overpayment credit ({unapplied:C}) has already " +
                    $"been applied or refunded (available credit is only {creditBalance:C}). Reverse the credit " +
                    $"application(s)/refund(s) first, then void this payment.");
        }

        await VoidLedgerEntryAsync(clientId, paymentId, payment.Date, "payment", reason, ct);

        await payments.VoidAsync(clientId, paymentId, ct);
        return (await payments.GetPaymentAsync(clientId, paymentId, ct))!;
    }

    public async Task<WriteOff> RecordWriteOffAsync(Guid clientId, WriteOffCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Allocations.Count == 0 || command.Allocations.Any(a => a.Amount <= 0m))
            throw new InvalidOperationException("A write-off needs positive allocations.");
        await ValidateAllocationsAsync(clientId, command.CustomerId, command.Allocations, ct);
        WriteOffBody body = new(command.CustomerId, command.Date, command.Memo);
        WriteOff recorded = await payments.RecordWriteOffAsync(clientId, body, ct);
        PaymentPostingAccounts posting = await accounts.GetAsync(clientId, ct);
        await ledger.PostAsync(clientId, PaymentPosting.ComposeWriteOff(recorded.Id, body, command.Allocations, posting), ct);
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

    public async Task<CreditNote> RecordCreditNoteAsync(Guid clientId, CreditNoteCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Allocations.Count == 0 || command.Allocations.Any(a => a.Amount <= 0m))
            throw new InvalidOperationException("A credit note needs positive allocations.");
        await ValidateAllocationsAsync(clientId, command.CustomerId, command.Allocations, ct);
        CreditNoteBody body = new(command.CustomerId, command.Date, command.Memo);
        CreditNote recorded = await payments.RecordCreditNoteAsync(clientId, body, ct);
        PaymentPostingAccounts posting = await accounts.GetAsync(clientId, ct);
        await ledger.PostAsync(clientId, PaymentPosting.ComposeCreditNote(recorded.Id, body, command.Allocations, posting), ct);
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

    /// <summary>
    /// Total amount applied to one invoice — the READ path. Folded Posted-only from the ledger's
    /// Invoice-axis A/R subledger (reads reflect only what is actually on the books): applied =
    /// invoice.Total − open, where open is the fold's signed balance for this invoice. When the invoice
    /// carries no on-the-books A/R line yet (its own issue entry is still PendingApproval), <c>open</c>
    /// defaults to <c>invoice.Total</c> — i.e. applied = 0 — so a freshly issued but unapproved invoice
    /// reads as fully open (not Paid). This mirrors how <see cref="ListInvoiceViewsAsync"/> already
    /// defaults an absent fold entry to Total.
    /// </summary>
    private async Task<decimal> AppliedToInvoiceAsync(Guid clientId, Invoice invoice, CancellationToken ct)
    {
        PaymentPostingAccounts posting = await accounts.GetAsync(clientId, ct);
        IReadOnlyList<SubledgerLineResponse> fold =
            await ledger.GetSubledgerAsync(clientId, posting.ReceivableAccountId, "Invoice", null, ct);
        decimal open = fold.FirstOrDefault(l => l.DimensionValue == invoice.Id)?.Balance ?? invoice.Total;
        return invoice.Total - open;
    }

    /// <summary>
    /// Total amount reserved against one invoice — the WRITE-PATH validation computation used by
    /// <see cref="ValidateAllocationsAsync"/>. Folded PENDING-INCLUSIVE (Posted + PendingApproval,
    /// non-void) so an unapproved relief (payment allocation, write-off, credit note/application) already
    /// recorded against the invoice reserves against it: a second, also-unapproved relief then fails this
    /// check instead of both passing and over-relieving the invoice once approved. Deliberately NOT used
    /// by any read path — reads must stay Posted-only (see <see cref="AppliedToInvoiceAsync"/>).
    /// </summary>
    private async Task<decimal> ReservedAgainstInvoiceAsync(Guid clientId, Invoice invoice, CancellationToken ct)
    {
        PaymentPostingAccounts posting = await accounts.GetAsync(clientId, ct);
        IReadOnlyList<SubledgerLineResponse> fold = await ledger.GetSubledgerAsync(
            clientId, posting.ReceivableAccountId, "Invoice", null, ct, includePending: true);
        decimal open = fold.FirstOrDefault(l => l.DimensionValue == invoice.Id)?.Balance ?? invoice.Total;
        return invoice.Total - open;
    }
}
