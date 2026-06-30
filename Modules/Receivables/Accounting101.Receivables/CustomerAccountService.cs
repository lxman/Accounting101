namespace Accounting101.Receivables;

/// <summary>Assembles the read-only <see cref="CustomerAccountView"/> for one customer by reading its
/// documents and folding them with <see cref="CustomerAccountBuilder"/>. Read-only; computes, never stores.</summary>
public sealed class CustomerAccountService(ICustomerStore customers, IInvoiceStore invoices, IPaymentStore payments)
{
    public async Task<CustomerAccountView?> GetAccountAsync(
        Guid clientId, Guid customerId, DateOnly asOf, CancellationToken ct = default)
    {
        Customer? customer = await customers.GetAsync(clientId, customerId, ct);
        if (customer is null) return null;

        IReadOnlyList<Invoice> invs = await invoices.GetByCustomerAsync(clientId, customerId, ct);
        IReadOnlyList<Payment> ps = await payments.GetPaymentsByCustomerAsync(clientId, customerId, ct);
        IReadOnlyList<CreditApplication> cs = await payments.GetCreditApplicationsByCustomerAsync(clientId, customerId, ct);
        IReadOnlyList<WriteOff> ws = await payments.GetWriteOffsByCustomerAsync(clientId, customerId, ct);
        IReadOnlyList<CreditNote> ns = await payments.GetCreditNotesByCustomerAsync(clientId, customerId, ct);
        IReadOnlyList<Refund> rs = await payments.GetRefundsByCustomerAsync(clientId, customerId, ct);

        Dictionary<Guid, decimal> applied = CustomerAccountBuilder.AppliedByInvoice(ps, cs, ws, ns);
        IReadOnlyList<OpenInvoiceLine> open = CustomerAccountBuilder.OpenInvoices(invs, applied, asOf);
        decimal credit = ps.Where(p => !p.Voided).Sum(p => p.Unapplied)
                         - cs.Where(c => !c.Voided).Sum(c => c.Applied)
                         - rs.Where(r => !r.Voided).Sum(r => r.Amount);

        return new CustomerAccountView(
            customer,
            CustomerAccountBuilder.ArBalance(open),
            credit,
            CustomerAccountBuilder.Aging(open),
            open,
            CustomerAccountBuilder.Statement(invs, ps, ns, ws, cs),
            CustomerAccountBuilder.CreditActivity(ps, cs, rs));
    }
}
