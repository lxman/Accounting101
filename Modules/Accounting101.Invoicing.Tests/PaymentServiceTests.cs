using Accounting101.Invoicing;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Invoicing.Tests;

public sealed class PaymentServiceTests
{
    private static readonly PaymentPostingAccounts Accounts = new()
    {
        ReceivableAccountId = Guid.NewGuid(), CashAccountId = Guid.NewGuid(), CustomerCreditsAccountId = Guid.NewGuid(),
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
        Invoice issued = await invoices.FinalizeAsync(clientId, draft.Id);

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
}

internal sealed class FixedPaymentAccountsProvider(PaymentPostingAccounts accounts) : IPaymentAccountsProvider
{
    public Task<PaymentPostingAccounts> GetAsync(Guid clientId, CancellationToken ct = default) => Task.FromResult(accounts);
}
