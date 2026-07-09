using Accounting101.Ledger.Contracts;

namespace Accounting101.Receivables;

/// <summary>Assembles the read-only <see cref="CustomerAccountView"/> for one customer by reading its
/// documents and folding the ledger for every AR-derived figure — the Invoice-axis and Customer-axis
/// subledgers are the single source of truth for what's actually open (on the books). Read-only; computes,
/// never stores.</summary>
public sealed class CustomerAccountService(
    ICustomerStore customers, IInvoiceStore invoices, IPaymentStore payments,
    IPaymentAccountsProvider accountsProvider, ILedgerClient ledger)
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

        PaymentPostingAccounts accounts = await accountsProvider.GetAsync(clientId, ct);

        IReadOnlyList<SubledgerLineResponse> arByInvoice =
            await ledger.GetSubledgerAsync(clientId, accounts.ReceivableAccountId, "Invoice", asOf, ct);
        Dictionary<Guid, decimal> openByInvoice = arByInvoice.ToDictionary(l => l.DimensionValue, l => l.Balance);
        // applied = total - open; OpenInvoices keeps its existing (total, applied) contract. An invoice
        // absent from the fold (its own AR line not yet on the books) defaults to fully open.
        Dictionary<Guid, decimal> applied = invs.ToDictionary(
            i => i.Id, i => i.Total - openByInvoice.GetValueOrDefault(i.Id, i.Total));

        IReadOnlyList<OpenInvoiceLine> open = CustomerAccountBuilder.OpenInvoices(invs, applied, asOf);

        // Customer Credits is a liability (credit-normal); the ledger's debit-positive fold reads a
        // positive available credit as NEGATIVE — negate it to present a positive balance.
        decimal credit = (await ledger.GetSubledgerAsync(clientId, accounts.CustomerCreditsAccountId, "Customer", asOf, ct))
            .Where(l => l.DimensionValue == customerId).Sum(l => -l.Balance);

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
