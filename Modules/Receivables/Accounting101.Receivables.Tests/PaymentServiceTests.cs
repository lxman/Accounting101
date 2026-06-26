using Accounting101.Receivables;
using Accounting101.Ledger.Contracts;
using Accounting101.Settlement;

namespace Accounting101.Receivables.Tests;

public sealed class PaymentServiceTests
{
    private static readonly PaymentPostingAccounts Accounts = new()
    {
        ReceivableAccountId = Guid.NewGuid(), CashAccountId = Guid.NewGuid(), CustomerCreditsAccountId = Guid.NewGuid(),
        BadDebtExpenseAccountId = Guid.NewGuid(), SalesReturnsAccountId = Guid.NewGuid(),
    };

    private sealed record Harness(PaymentService Service, FakeLedgerClient Ledger, InMemoryInvoiceStore Invoices, InMemoryPaymentStore Payments);

    private static async Task<(Harness h, Guid clientId, Guid customerId, Invoice invoice)> SetupWithIssuedInvoiceAsync(decimal invoiceTotal)
    {
        Guid clientId = Guid.NewGuid();
        Guid customerId = Guid.NewGuid();
        InMemoryInvoiceStore invoices = new();
        Invoice draft = await invoices.CreateDraftAsync(clientId, new InvoiceBody(
            customerId, new DateOnly(2026, 3, 1), null, 0m, null,
            [new LineBody("Services", 1m, invoiceTotal, false)]));
        Invoice issued = await invoices.PromoteDraftAsync(clientId, draft.Id);

        FakeLedgerClient ledger = new();
        InMemoryPaymentStore payments = new();
        PaymentService service = new(payments, invoices, new FixedPaymentAccountsProvider(Accounts), ledger);
        return (new Harness(service, ledger, invoices, payments), clientId, customerId, issued);
    }

    [Fact]
    public async Task Records_a_payment_and_posts_a_pending_settlement_entry()
    {
        (Harness h, Guid clientId, Guid customerId, Invoice invoice) = await SetupWithIssuedInvoiceAsync(100m);

        PaymentBody body = new(customerId, new DateOnly(2026, 3, 31), 40m, null, [new Allocation(invoice.Id, 40m)]);
        Payment recorded = await h.Service.RecordPaymentAsync(clientId, body);

        Assert.NotEqual(Guid.Empty, recorded.Id);
        PostEntryRequest entry = Assert.Single(h.Ledger.Posted);
        Assert.Equal("Payment", entry.SourceType);
        Assert.Equal(recorded.Id, entry.SourceRef);
    }

    [Fact]
    public async Task Rejects_a_payment_whose_allocations_exceed_its_amount()
    {
        (Harness h, Guid clientId, Guid customerId, Invoice invoice) = await SetupWithIssuedInvoiceAsync(100m);

        PaymentBody body = new(customerId, new DateOnly(2026, 3, 31), 40m, null, [new Allocation(invoice.Id, 60m)]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.RecordPaymentAsync(clientId, body));
        Assert.Empty(h.Ledger.Posted);
    }

    [Fact]
    public async Task Rejects_an_allocation_exceeding_an_invoice_open_balance()
    {
        (Harness h, Guid clientId, Guid customerId, Invoice invoice) = await SetupWithIssuedInvoiceAsync(100m);

        PaymentBody body = new(customerId, new DateOnly(2026, 3, 31), 150m, null, [new Allocation(invoice.Id, 150m)]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.RecordPaymentAsync(clientId, body));
    }

    [Fact]
    public async Task Invoice_view_reflects_a_partial_payment()
    {
        (Harness h, Guid clientId, Guid customerId, Invoice invoice) = await SetupWithIssuedInvoiceAsync(100m);
        await h.Service.RecordPaymentAsync(clientId, new PaymentBody(customerId, new DateOnly(2026, 3, 31), 40m, null, [new Allocation(invoice.Id, 40m)]));

        InvoiceView? view = await h.Service.GetInvoiceViewAsync(clientId, invoice.Id);

        Assert.NotNull(view);
        Assert.Equal(60m, view!.OpenBalance);
        Assert.Equal(SettlementStatus.PartiallyPaid, view.SettlementStatus);
    }

    [Fact]
    public async Task Over_payment_raises_the_customer_credit_balance()
    {
        (Harness h, Guid clientId, Guid customerId, Invoice invoice) = await SetupWithIssuedInvoiceAsync(100m);
        await h.Service.RecordPaymentAsync(clientId, new PaymentBody(customerId, new DateOnly(2026, 3, 31), 150m, null, [new Allocation(invoice.Id, 100m)]));

        Assert.Equal(50m, await h.Service.GetCustomerCreditBalanceAsync(clientId, customerId));
        InvoiceView? view = await h.Service.GetInvoiceViewAsync(clientId, invoice.Id);
        Assert.Equal(SettlementStatus.Paid, view!.SettlementStatus);
    }

    [Fact]
    public async Task Applies_existing_credit_to_an_invoice_and_lowers_the_balance()
    {
        (Harness h, Guid clientId, Guid customerId, Invoice first) = await SetupWithIssuedInvoiceAsync(100m);
        // Create $50 of credit via over-payment on the first invoice.
        await h.Service.RecordPaymentAsync(clientId, new PaymentBody(customerId, new DateOnly(2026, 3, 31), 150m, null, [new Allocation(first.Id, 100m)]));
        // A second issued invoice to apply credit against.
        Invoice draft2 = await h.Invoices.CreateDraftAsync(clientId, new InvoiceBody(customerId, new DateOnly(2026, 4, 1), null, 0m, null, [new LineBody("More", 1m, 100m, false)]));
        Invoice second = await h.Invoices.PromoteDraftAsync(clientId, draft2.Id);

        CreditApplication applied = await h.Service.RecordCreditApplicationAsync(clientId,
            new CreditApplicationBody(customerId, new DateOnly(2026, 4, 2), [new Allocation(second.Id, 50m)]));

        Assert.Equal(50m, applied.Applied);
        Assert.Equal(0m, await h.Service.GetCustomerCreditBalanceAsync(clientId, customerId));
        Assert.Equal(50m, (await h.Service.GetInvoiceViewAsync(clientId, second.Id))!.OpenBalance);
        Assert.Contains(h.Ledger.Posted, e => e.SourceType == "CreditApplication");
    }

    [Fact]
    public async Task Rejects_a_credit_application_exceeding_available_credit()
    {
        (Harness h, Guid clientId, Guid customerId, Invoice invoice) = await SetupWithIssuedInvoiceAsync(100m);
        // No credit created yet.
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.RecordCreditApplicationAsync(clientId,
            new CreditApplicationBody(customerId, new DateOnly(2026, 4, 2), [new Allocation(invoice.Id, 25m)])));
    }

    [Fact]
    public async Task Voiding_a_payment_restores_the_invoice_open_balance()
    {
        (Harness h, Guid clientId, Guid customerId, Invoice invoice) = await SetupWithIssuedInvoiceAsync(100m);
        Payment p = await h.Service.RecordPaymentAsync(clientId, new PaymentBody(customerId, new DateOnly(2026, 3, 31), 40m, null, [new Allocation(invoice.Id, 40m)]));
        Assert.Equal(60m, (await h.Service.GetInvoiceViewAsync(clientId, invoice.Id))!.OpenBalance);

        await h.Service.VoidPaymentAsync(clientId, p.Id);

        Assert.Equal(100m, (await h.Service.GetInvoiceViewAsync(clientId, invoice.Id))!.OpenBalance);
        Assert.Equal(SettlementStatus.Open, (await h.Service.GetInvoiceViewAsync(clientId, invoice.Id))!.SettlementStatus);
    }

    [Fact]
    public async Task Lists_customer_invoices_filtered_by_settlement()
    {
        (Harness h, Guid clientId, Guid customerId, Invoice first) = await SetupWithIssuedInvoiceAsync(100m);
        // Pay the first invoice in full -> Paid.
        await h.Service.RecordPaymentAsync(clientId, new PaymentBody(customerId, new DateOnly(2026, 3, 31), 100m, null, [new Allocation(first.Id, 100m)]));
        // A second, unpaid invoice -> Open.
        Invoice d2 = await h.Invoices.CreateDraftAsync(clientId, new InvoiceBody(customerId, new DateOnly(2026, 4, 1), null, 0m, null, [new LineBody("More", 1m, 100m, false)]));
        Invoice second = await h.Invoices.PromoteDraftAsync(clientId, d2.Id);

        IReadOnlyList<InvoiceView> open = await h.Service.ListInvoiceViewsAsync(clientId, customerId, SettlementFilter.Open);
        IReadOnlyList<InvoiceView> paid = await h.Service.ListInvoiceViewsAsync(clientId, customerId, SettlementFilter.Paid);
        IReadOnlyList<InvoiceView> all = await h.Service.ListInvoiceViewsAsync(clientId, customerId, null);

        Assert.Equal(second.Id, Assert.Single(open).Invoice.Id);
        Assert.Equal(first.Id, Assert.Single(paid).Invoice.Id);
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task Voided_invoice_does_not_appear_in_list_views()
    {
        // Arrange: issue an invoice then void it.
        (Harness h, Guid clientId, Guid customerId, Invoice invoice) = await SetupWithIssuedInvoiceAsync(100m);
        await h.Invoices.VoidAsync(clientId, invoice.Id);

        // Act: query both the all-invoices list and the open-settlement filter.
        IReadOnlyList<InvoiceView> all  = await h.Service.ListInvoiceViewsAsync(clientId, customerId, null);
        IReadOnlyList<InvoiceView> open = await h.Service.ListInvoiceViewsAsync(clientId, customerId, SettlementFilter.Open);

        // Assert: a voided invoice is not a settleable receivable — it must not appear in either view.
        Assert.Empty(all);
        Assert.Empty(open);
    }

    // ── settlement-gating: only Issued invoices are settleable ──────────────

    [Fact]
    public async Task Draft_invoice_does_not_appear_in_list_views()
    {
        // Arrange: create a draft invoice (never issue it).
        Guid clientId = Guid.NewGuid();
        Guid customerId = Guid.NewGuid();
        InMemoryInvoiceStore invoices = new();
        await invoices.CreateDraftAsync(clientId, new InvoiceBody(
            customerId, new DateOnly(2026, 3, 1), null, 0m, null,
            [new LineBody("Services", 1m, 100m, false)]));

        InMemoryPaymentStore payments = new();
        FakeLedgerClient ledger = new();
        PaymentService service = new(payments, invoices, new FixedPaymentAccountsProvider(Accounts), ledger);

        // Act: query both the all-invoices list and the open-settlement filter.
        IReadOnlyList<InvoiceView> all  = await service.ListInvoiceViewsAsync(clientId, customerId, null);
        IReadOnlyList<InvoiceView> open = await service.ListInvoiceViewsAsync(clientId, customerId, SettlementFilter.Open);

        // Assert: a draft invoice must not appear in either settlement view.
        Assert.Empty(all);
        Assert.Empty(open);
    }

    [Fact]
    public async Task Allocating_payment_to_draft_invoice_is_rejected_with_issued_message()
    {
        // Arrange: create a draft invoice (never issue it).
        Guid clientId = Guid.NewGuid();
        Guid customerId = Guid.NewGuid();
        InMemoryInvoiceStore invoices = new();
        Invoice draft = await invoices.CreateDraftAsync(clientId, new InvoiceBody(
            customerId, new DateOnly(2026, 3, 1), null, 0m, null,
            [new LineBody("Services", 1m, 100m, false)]));

        InMemoryPaymentStore payments = new();
        FakeLedgerClient ledger = new();
        PaymentService service = new(payments, invoices, new FixedPaymentAccountsProvider(Accounts), ledger);

        // Act & Assert: paying a draft must throw with a message mentioning "issued".
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RecordPaymentAsync(clientId,
                new PaymentBody(customerId, new DateOnly(2026, 3, 31), 100m, null, [new Allocation(draft.Id, 100m)])));
        Assert.Contains("issued", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Allocating_payment_to_void_invoice_is_rejected()
    {
        // Arrange: issue then void.
        (Harness h, Guid clientId, Guid customerId, Invoice invoice) = await SetupWithIssuedInvoiceAsync(100m);
        await h.Invoices.VoidAsync(clientId, invoice.Id);

        // Act & Assert: paying a void invoice must throw.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Service.RecordPaymentAsync(clientId,
                new PaymentBody(customerId, new DateOnly(2026, 3, 31), 100m, null, [new Allocation(invoice.Id, 100m)])));
    }

    [Fact]
    public async Task Allocating_payment_to_issued_invoice_succeeds()
    {
        // Arrange: the standard setup already issues the invoice.
        (Harness h, Guid clientId, Guid customerId, Invoice invoice) = await SetupWithIssuedInvoiceAsync(100m);

        // Act: pay in full — must not throw.
        Payment recorded = await h.Service.RecordPaymentAsync(clientId,
            new PaymentBody(customerId, new DateOnly(2026, 3, 31), 100m, null, [new Allocation(invoice.Id, 100m)]));

        // Assert: payment recorded and settled.
        Assert.NotEqual(Guid.Empty, recorded.Id);
        InvoiceView? view = await h.Service.GetInvoiceViewAsync(clientId, invoice.Id);
        Assert.Equal(SettlementStatus.Paid, view!.SettlementStatus);
    }
}

internal sealed class FixedPaymentAccountsProvider(PaymentPostingAccounts accounts) : IPaymentAccountsProvider
{
    public Task<PaymentPostingAccounts> GetAsync(Guid clientId, CancellationToken ct = default) => Task.FromResult(accounts);
}
