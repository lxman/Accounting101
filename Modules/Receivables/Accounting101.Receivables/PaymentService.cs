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

        Dictionary<Guid, decimal> applied = new();
        foreach (Allocation a in ps.Where(p => !p.Voided).SelectMany(p => p.Allocations)
                     .Concat(cs.Where(c => !c.Voided).SelectMany(c => c.Allocations)))
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

    /// <summary>Unapplied customer credit = non-voided payment remainders minus non-voided credit applications.</summary>
    public async Task<decimal> GetCustomerCreditBalanceAsync(Guid clientId, Guid customerId, CancellationToken ct = default)
    {
        IReadOnlyList<Payment> ps = await payments.GetPaymentsByCustomerAsync(clientId, customerId, ct);
        IReadOnlyList<CreditApplication> cs = await payments.GetCreditApplicationsByCustomerAsync(clientId, customerId, ct);
        decimal created = ps.Where(p => !p.Voided).Sum(p => p.Unapplied);
        decimal spent = cs.Where(c => !c.Voided).Sum(c => c.Applied);
        return created - spent;
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

        IReadOnlyList<EntryResponse> spawned = await ledger.GetEntriesBySourceRefAsync(clientId, paymentId, ct);
        EntryResponse settlement = spawned.FirstOrDefault(e => e is { Status: "Active", ReversalOf: null })
            ?? throw new InvalidOperationException($"No entry found for payment {paymentId} to void.");

        if (settlement.Posting == "Posted")
            await ledger.ReverseAsync(clientId, settlement.Id, new ReverseRequest(payment.Date, reason ?? $"Voided payment {paymentId}"), ct);
        else
            await ledger.VoidAsync(clientId, settlement.Id, new VoidRequest(reason ?? $"Voided payment {paymentId}"), ct);

        await payments.VoidAsync(clientId, paymentId, ct);
        return (await payments.GetPaymentAsync(clientId, paymentId, ct))!;
    }

    /// <summary>Total non-voided allocations (payments + credit applications) applied to one invoice.</summary>
    private async Task<decimal> AppliedToInvoiceAsync(Guid clientId, Guid customerId, Guid invoiceId, CancellationToken ct)
    {
        IReadOnlyList<Payment> ps = await payments.GetPaymentsByCustomerAsync(clientId, customerId, ct);
        IReadOnlyList<CreditApplication> cs = await payments.GetCreditApplicationsByCustomerAsync(clientId, customerId, ct);
        decimal fromPayments = ps.Where(p => !p.Voided).SelectMany(p => p.Allocations).Where(x => x.TargetId == invoiceId).Sum(x => x.Amount);
        decimal fromCredits = cs.Where(c => !c.Voided).SelectMany(c => c.Allocations).Where(x => x.TargetId == invoiceId).Sum(x => x.Amount);
        return fromPayments + fromCredits;
    }
}
