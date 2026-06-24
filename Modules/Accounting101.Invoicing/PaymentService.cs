using Accounting101.Ledger.Contracts;

namespace Accounting101.Invoicing;

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
            Invoice invoice = await invoices.GetAsync(clientId, a.InvoiceId, ct)
                ?? throw new InvalidOperationException($"Invoice {a.InvoiceId} does not exist.");
            if (invoice.Status == InvoiceStatus.Void)
                throw new InvalidOperationException($"Invoice {a.InvoiceId} is voided.");
            if (invoice.CustomerId != customerId)
                throw new InvalidOperationException($"Invoice {a.InvoiceId} belongs to a different customer.");

            decimal alreadyApplied = await AppliedToInvoiceAsync(clientId, customerId, a.InvoiceId, ct);
            if (alreadyApplied + a.Amount > invoice.Total)
                throw new InvalidOperationException($"Allocation to invoice {a.InvoiceId} exceeds its open balance.");
        }
    }

    public async Task<InvoiceView?> GetInvoiceViewAsync(Guid clientId, Guid invoiceId, CancellationToken ct = default)
    {
        Invoice? invoice = await invoices.GetAsync(clientId, invoiceId, ct);
        if (invoice is null) return null;
        decimal applied = await AppliedToInvoiceAsync(clientId, invoice.CustomerId, invoiceId, ct);
        return new InvoiceView(invoice, Settlement.OpenBalance(invoice.Total, applied), Settlement.Status(invoice.Total, applied));
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

    /// <summary>Total non-voided allocations (payments + credit applications) applied to one invoice.</summary>
    private async Task<decimal> AppliedToInvoiceAsync(Guid clientId, Guid customerId, Guid invoiceId, CancellationToken ct)
    {
        IReadOnlyList<Payment> ps = await payments.GetPaymentsByCustomerAsync(clientId, customerId, ct);
        IReadOnlyList<CreditApplication> cs = await payments.GetCreditApplicationsByCustomerAsync(clientId, customerId, ct);
        decimal fromPayments = ps.Where(p => !p.Voided).SelectMany(p => p.Allocations).Where(x => x.InvoiceId == invoiceId).Sum(x => x.Amount);
        decimal fromCredits = cs.Where(c => !c.Voided).SelectMany(c => c.Allocations).Where(x => x.InvoiceId == invoiceId).Sum(x => x.Amount);
        return fromPayments + fromCredits;
    }
}
