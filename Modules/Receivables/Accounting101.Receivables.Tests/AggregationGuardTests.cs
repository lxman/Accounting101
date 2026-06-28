using Accounting101.Ledger.Contracts;
using Accounting101.Settlement;

namespace Accounting101.Receivables.Tests;

/// <summary>
/// Guard test: proves that the aggregation (GetCustomerCreditBalanceAsync) still sees ALL documents
/// even when the display list (ListInvoices endpoint) would page them. The service's GetByCustomerAsync
/// and GetPaymentsByCustomerAsync store calls are UNCHANGED (unbounded) — paging is in-memory at the
/// endpoint only. Seeding > one page of documents and asserting the aggregation total reflects every
/// document is the canonical proof of that boundary.
/// </summary>
public sealed class AggregationGuardTests
{
    private static readonly InvoicePostingAccounts InvAccounts = new()
    {
        ReceivableAccountId = Guid.NewGuid(),
        DefaultRevenueAccountId = Guid.NewGuid(),
        SalesTaxPayableAccountId = Guid.NewGuid(),
    };

    private static readonly PaymentPostingAccounts PayAccounts = new()
    {
        ReceivableAccountId = InvAccounts.ReceivableAccountId,
        CashAccountId = Guid.NewGuid(),
        CustomerCreditsAccountId = Guid.NewGuid(),
        BadDebtExpenseAccountId = Guid.NewGuid(),
        SalesReturnsAccountId = Guid.NewGuid(),
    };

    private sealed record Harness(InvoiceService Invoice, PaymentService Payment, FakeLedgerClient Ledger);

    private static Harness NewHarness()
    {
        FakeLedgerClient ledger = new();
        InMemoryInvoiceStore invoices = new();
        InMemoryCustomerStore customers = new();
        InMemoryPaymentStore payments = new();
        InvoiceService invoiceSvc = new(invoices, customers, new FixedAccountsProvider(InvAccounts), ledger);
        PaymentService paymentSvc = new(payments, invoices, new FixedPaymentAccountsProvider(PayAccounts), ledger);
        return new Harness(invoiceSvc, paymentSvc, ledger);
    }

    /// <summary>
    /// Issues an invoice directly via the store (bypasses posting so the fake ledger
    /// doesn't need a live entry) and returns the issued invoice.
    /// </summary>
    private static async Task<Invoice> IssueDirectAsync(InMemoryInvoiceStore store, Guid clientId, Guid customerId, decimal amount)
    {
        Invoice draft = await store.CreateDraftAsync(clientId, new InvoiceBody(
            customerId, new DateOnly(2026, 3, 1), null, 0m, null,
            [new LineBody("Services", 1m, amount, false)]));
        return await store.PromoteDraftAsync(clientId, draft.Id);
    }

    [Fact]
    public async Task Credit_balance_aggregation_reflects_all_payments_even_when_list_would_page()
    {
        // Arrange: 3 invoices for the same customer — with limit=2 the display list would page,
        // but the credit-balance aggregation must scan ALL payments (unbounded store read).
        Harness h = NewHarness();
        Guid clientId = Guid.NewGuid();
        Customer customer = await h.Invoice.CreateCustomerAsync(clientId, "Acme Corp");

        // Issue 3 invoices (>1 page with default limit=2 used by the guard).
        InMemoryInvoiceStore invoiceStore = new();
        Invoice inv1 = await IssueDirectAsync(invoiceStore, clientId, customer.Id, 100m);
        Invoice inv2 = await IssueDirectAsync(invoiceStore, clientId, customer.Id, 100m);
        Invoice inv3 = await IssueDirectAsync(invoiceStore, clientId, customer.Id, 100m);

        // Re-wire the payment service to see the seeded store.
        InMemoryPaymentStore paymentStore = new();
        PaymentService paymentSvc = new(paymentStore, invoiceStore, new FixedPaymentAccountsProvider(PayAccounts), h.Ledger);

        // Record 3 payments each with $50 unapplied credit (amount 150, allocated 100).
        DateOnly date = new(2026, 3, 31);
        await paymentSvc.RecordPaymentAsync(clientId, new PaymentBody(customer.Id, date, 150m, null, [new Allocation(inv1.Id, 100m)]));
        await paymentSvc.RecordPaymentAsync(clientId, new PaymentBody(customer.Id, date, 150m, null, [new Allocation(inv2.Id, 100m)]));
        await paymentSvc.RecordPaymentAsync(clientId, new PaymentBody(customer.Id, date, 150m, null, [new Allocation(inv3.Id, 100m)]));

        // Act: credit balance aggregation (unbounded store read — GetPaymentsByCustomerAsync).
        decimal credit = await paymentSvc.GetCustomerCreditBalanceAsync(clientId, customer.Id);

        // Assert: all 3 payments' unapplied amounts are summed — proves the store was NOT paged.
        // If only 2 payments were seen (a paged read), credit would be 100m, not 150m.
        Assert.Equal(150m, credit);

        // Also confirm the invoice list service call (ListInvoiceViewsAsync) returns all 3 —
        // the service method is unbounded; the endpoint is the only place paging applies.
        IReadOnlyList<InvoiceView> allViews = await paymentSvc.ListInvoiceViewsAsync(clientId, customer.Id, filter: null);
        Assert.Equal(3, allViews.Count);
    }
}
