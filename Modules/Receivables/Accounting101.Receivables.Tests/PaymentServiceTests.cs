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

    private static readonly Guid DummyRevenueAccountId = Guid.NewGuid();

    private sealed record Harness(PaymentService Service, FakeLedgerClient Ledger, InMemoryInvoiceStore Invoices, InMemoryPaymentStore Payments);

    /// <summary>
    /// PaymentService now derives every open balance/applied/credit figure by folding the ledger
    /// (<c>ILedgerClient.GetSubledgerAsync</c>), not the module's stored <c>Allocation[]</c>. This harness's
    /// invoices are created directly through <see cref="InMemoryInvoiceStore"/> — bypassing InvoiceService,
    /// which is what posts an invoice's own AR-debit line in production — so the fold would see only the
    /// relief-side credit lines PaymentService posts, never the invoice's own debit. Seed that debit here so
    /// the fold starts from the correct open balance, mirroring what InvoiceService's issue recipe posts.
    /// Uses <see cref="FakeLedgerClient.SeedEntry"/> (not <c>PostAsync</c>) so this bookkeeping doesn't show
    /// up in <c>ledger.Posted</c>/<c>LastPosted</c> assertions, which are about what PaymentService itself posts.
    /// </summary>
    private static void PostInvoiceArDebit(FakeLedgerClient ledger, Guid customerId, Invoice invoice) =>
        ledger.SeedEntry(new PostEntryRequest(
            Id: null, EffectiveDate: invoice.IssueDate, Reference: invoice.Number, Memo: null,
            Lines:
            [
                new PostLineRequest(Accounts.ReceivableAccountId, "Debit", invoice.Total,
                    Dimensions: new Dictionary<string, Guid> { ["Customer"] = customerId, ["Invoice"] = invoice.Id }),
                new PostLineRequest(DummyRevenueAccountId, "Credit", invoice.Total),
            ],
            SourceRef: invoice.Id, SourceType: "Invoice"));

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
        PostInvoiceArDebit(ledger, customerId, issued);
        InMemoryPaymentStore payments = new();
        PaymentService service = new(payments, invoices, new FixedPaymentAccountsProvider(Accounts), ledger);
        return (new Harness(service, ledger, invoices, payments), clientId, customerId, issued);
    }

    [Fact]
    public async Task Records_a_payment_and_posts_a_pending_settlement_entry()
    {
        (Harness h, Guid clientId, Guid customerId, Invoice invoice) = await SetupWithIssuedInvoiceAsync(100m);

        PaymentCommand command = new(customerId, new DateOnly(2026, 3, 31), 40m, null, [new Allocation(invoice.Id, 40m)]);
        Payment recorded = await h.Service.RecordPaymentAsync(clientId, command);

        Assert.NotEqual(Guid.Empty, recorded.Id);
        PostEntryRequest entry = Assert.Single(h.Ledger.Posted);
        Assert.Equal("Payment", entry.SourceType);
        Assert.Equal(recorded.Id, entry.SourceRef);
    }

    [Fact]
    public async Task Rejects_a_payment_whose_allocations_exceed_its_amount()
    {
        (Harness h, Guid clientId, Guid customerId, Invoice invoice) = await SetupWithIssuedInvoiceAsync(100m);

        PaymentCommand command = new(customerId, new DateOnly(2026, 3, 31), 40m, null, [new Allocation(invoice.Id, 60m)]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.RecordPaymentAsync(clientId, command));
        Assert.Empty(h.Ledger.Posted);
    }

    [Fact]
    public async Task Rejects_an_allocation_exceeding_an_invoice_open_balance()
    {
        (Harness h, Guid clientId, Guid customerId, Invoice invoice) = await SetupWithIssuedInvoiceAsync(100m);

        PaymentCommand command = new(customerId, new DateOnly(2026, 3, 31), 150m, null, [new Allocation(invoice.Id, 150m)]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.RecordPaymentAsync(clientId, command));
    }

    [Fact]
    public async Task Invoice_view_reflects_a_partial_payment()
    {
        (Harness h, Guid clientId, Guid customerId, Invoice invoice) = await SetupWithIssuedInvoiceAsync(100m);
        await h.Service.RecordPaymentAsync(clientId, new PaymentCommand(customerId, new DateOnly(2026, 3, 31), 40m, null, [new Allocation(invoice.Id, 40m)]));

        InvoiceView? view = await h.Service.GetInvoiceViewAsync(clientId, invoice.Id);

        Assert.NotNull(view);
        Assert.Equal(60m, view!.OpenBalance);
        Assert.Equal(SettlementStatus.PartiallyPaid, view.SettlementStatus);
    }

    [Fact]
    public async Task Over_payment_raises_the_customer_credit_balance()
    {
        (Harness h, Guid clientId, Guid customerId, Invoice invoice) = await SetupWithIssuedInvoiceAsync(100m);
        await h.Service.RecordPaymentAsync(clientId, new PaymentCommand(customerId, new DateOnly(2026, 3, 31), 150m, null, [new Allocation(invoice.Id, 100m)]));

        Assert.Equal(50m, await h.Service.GetCustomerCreditBalanceAsync(clientId, customerId));
        InvoiceView? view = await h.Service.GetInvoiceViewAsync(clientId, invoice.Id);
        Assert.Equal(SettlementStatus.Paid, view!.SettlementStatus);
    }

    [Fact]
    public async Task Applies_existing_credit_to_an_invoice_and_lowers_the_balance()
    {
        (Harness h, Guid clientId, Guid customerId, Invoice first) = await SetupWithIssuedInvoiceAsync(100m);
        // Create $50 of credit via over-payment on the first invoice.
        await h.Service.RecordPaymentAsync(clientId, new PaymentCommand(customerId, new DateOnly(2026, 3, 31), 150m, null, [new Allocation(first.Id, 100m)]));
        // A second issued invoice to apply credit against.
        Invoice second = await IssueAnotherInvoiceAsync(h, clientId, customerId, 100m);

        CreditApplication applied = await h.Service.RecordCreditApplicationAsync(clientId,
            new CreditApplicationCommand(customerId, new DateOnly(2026, 4, 2), [new Allocation(second.Id, 50m)]));

        // CreditApplication no longer carries an Allocations array (or an Applied accessor derived from
        // one) — prove the 50 applied by folding it from the document's own posted entry instead.
        IReadOnlyList<EntryResponse> appliedEntries = await h.Ledger.GetEntriesBySourceRefAsync(clientId, applied.Id);
        EntryResponse appliedEntry = appliedEntries.Single(e => e.ReversalOf == null);
        Assert.Equal(50m, appliedEntry.Lines.Where(l => l.AccountId == Accounts.ReceivableAccountId).Sum(l => l.Amount));
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
            new CreditApplicationCommand(customerId, new DateOnly(2026, 4, 2), [new Allocation(invoice.Id, 25m)])));
    }

    [Fact]
    public async Task Voiding_a_payment_restores_the_invoice_open_balance()
    {
        (Harness h, Guid clientId, Guid customerId, Invoice invoice) = await SetupWithIssuedInvoiceAsync(100m);
        Payment p = await h.Service.RecordPaymentAsync(clientId, new PaymentCommand(customerId, new DateOnly(2026, 3, 31), 40m, null, [new Allocation(invoice.Id, 40m)]));
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
        await h.Service.RecordPaymentAsync(clientId, new PaymentCommand(customerId, new DateOnly(2026, 3, 31), 100m, null, [new Allocation(first.Id, 100m)]));
        // A second, unpaid invoice -> Open.
        Invoice second = await IssueAnotherInvoiceAsync(h, clientId, customerId, 100m);

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
                new PaymentCommand(customerId, new DateOnly(2026, 3, 31), 100m, null, [new Allocation(draft.Id, 100m)])));
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
                new PaymentCommand(customerId, new DateOnly(2026, 3, 31), 100m, null, [new Allocation(invoice.Id, 100m)])));
    }

    [Fact]
    public async Task Allocating_payment_to_issued_invoice_succeeds()
    {
        // Arrange: the standard setup already issues the invoice.
        (Harness h, Guid clientId, Guid customerId, Invoice invoice) = await SetupWithIssuedInvoiceAsync(100m);

        // Act: pay in full — must not throw.
        Payment recorded = await h.Service.RecordPaymentAsync(clientId,
            new PaymentCommand(customerId, new DateOnly(2026, 3, 31), 100m, null, [new Allocation(invoice.Id, 100m)]));

        // Assert: payment recorded and settled.
        Assert.NotEqual(Guid.Empty, recorded.Id);
        InvoiceView? view = await h.Service.GetInvoiceViewAsync(clientId, invoice.Id);
        Assert.Equal(SettlementStatus.Paid, view!.SettlementStatus);
    }

    // ── write-off & credit-note dispositions ────────────────────────────────

    /// <summary>Brief-style helper: returns the service plus the ids needed to exercise a disposition.</summary>
    private static async Task<(PaymentService service, FakeLedgerClient ledger, Guid clientId, Guid customer, Guid invoice)>
        SetupIssuedInvoiceAsync(decimal total)
    {
        (Harness h, Guid clientId, Guid customerId, Invoice invoice) = await SetupWithIssuedInvoiceAsync(total);
        return (h.Service, h.Ledger, clientId, customerId, invoice.Id);
    }

    [Fact]
    public async Task WriteOff_settles_invoice_and_records_balanced_entry()
    {
        (PaymentService service, FakeLedgerClient ledger, Guid clientId, Guid customer, Guid invoice) =
            await SetupIssuedInvoiceAsync(total: 250m);

        WriteOff wo = await service.RecordWriteOffAsync(clientId,
            new WriteOffCommand(customer, new DateOnly(2026, 3, 1), [new Allocation(invoice, 250m)], "uncollectible"));

        InvoiceView view = (await service.GetInvoiceViewAsync(clientId, invoice))!;
        Assert.Equal(0m, view.OpenBalance);
        Assert.Equal(SettlementStatus.Paid, view.SettlementStatus);
        PostEntryRequest entry = ledger.LastPosted!;
        Assert.Equal("WriteOff", entry.SourceType);
        Assert.Equal(wo.Id, entry.SourceRef);
    }

    [Fact]
    public async Task CreditNote_reduces_open_balance()
    {
        (PaymentService service, _, Guid clientId, Guid customer, Guid invoice) =
            await SetupIssuedInvoiceAsync(total: 100m);

        await service.RecordCreditNoteAsync(clientId,
            new CreditNoteCommand(customer, new DateOnly(2026, 3, 1), [new Allocation(invoice, 30m)], "partial return"));

        InvoiceView view = (await service.GetInvoiceViewAsync(clientId, invoice))!;
        Assert.Equal(70m, view.OpenBalance);
        Assert.Equal(SettlementStatus.PartiallyPaid, view.SettlementStatus);
    }

    [Fact]
    public async Task WriteOff_over_open_balance_is_rejected()
    {
        (PaymentService service, _, Guid clientId, Guid customer, Guid invoice) =
            await SetupIssuedInvoiceAsync(total: 100m);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecordWriteOffAsync(clientId,
            new WriteOffCommand(customer, new DateOnly(2026, 3, 1), [new Allocation(invoice, 150m)], null)));
    }

    [Fact]
    public async Task Void_write_off_restores_open_balance()
    {
        (PaymentService service, _, Guid clientId, Guid customer, Guid invoice) =
            await SetupIssuedInvoiceAsync(total: 250m);
        WriteOff wo = await service.RecordWriteOffAsync(clientId,
            new WriteOffCommand(customer, new DateOnly(2026, 3, 1), [new Allocation(invoice, 250m)], null));

        await service.VoidWriteOffAsync(clientId, wo.Id, "keyed in error");

        InvoiceView view = (await service.GetInvoiceViewAsync(clientId, invoice))!;
        Assert.Equal(250m, view.OpenBalance);
        Assert.Equal(SettlementStatus.Open, view.SettlementStatus);
    }

    [Fact]
    public async Task CreditNote_over_open_balance_is_rejected()
    {
        (PaymentService service, _, Guid clientId, Guid customer, Guid invoice) =
            await SetupIssuedInvoiceAsync(total: 100m);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecordCreditNoteAsync(clientId,
            new CreditNoteCommand(customer, new DateOnly(2026, 3, 1), [new Allocation(invoice, 150m)], null)));
    }

    [Fact]
    public async Task Void_credit_note_restores_open_balance()
    {
        (PaymentService service, _, Guid clientId, Guid customer, Guid invoice) =
            await SetupIssuedInvoiceAsync(total: 100m);
        CreditNote cn = await service.RecordCreditNoteAsync(clientId,
            new CreditNoteCommand(customer, new DateOnly(2026, 3, 1), [new Allocation(invoice, 40m)], null));
        Assert.Equal(60m, (await service.GetInvoiceViewAsync(clientId, invoice))!.OpenBalance);

        await service.VoidCreditNoteAsync(clientId, cn.Id, "issued in error");

        InvoiceView view = (await service.GetInvoiceViewAsync(clientId, invoice))!;
        Assert.Equal(100m, view.OpenBalance);
        Assert.Equal(SettlementStatus.Open, view.SettlementStatus);
    }

    [Fact]
    public async Task WriteOff_to_another_customers_invoice_is_rejected()
    {
        (PaymentService service, _, Guid clientId, _, Guid invoice) =
            await SetupIssuedInvoiceAsync(total: 100m);
        Guid otherCustomer = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecordWriteOffAsync(clientId,
            new WriteOffCommand(otherCustomer, new DateOnly(2026, 3, 1), [new Allocation(invoice, 50m)], null)));
    }

    [Fact]
    public async Task CreditNote_against_a_void_invoice_is_rejected()
    {
        (Harness h, Guid clientId, Guid customerId, Invoice invoice) = await SetupWithIssuedInvoiceAsync(100m);
        await h.Invoices.VoidAsync(clientId, invoice.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.RecordCreditNoteAsync(clientId,
            new CreditNoteCommand(customerId, new DateOnly(2026, 3, 1), [new Allocation(invoice.Id, 50m)], null)));
    }

    // ── customer refund disposition ──────────────────────────────────────────

    [Fact]
    public async Task Refund_draws_down_customer_credit_balance()
    {
        // Arrange: customer overpays a 100 invoice by 40 → 40 credit (reuse the overpayment setup).
        (PaymentService service, FakeLedgerClient ledger, Guid clientId, Guid customer, Guid invoice) =
            await SetupIssuedInvoiceAsync(total: 100m);
        await service.RecordPaymentAsync(clientId,
            new PaymentCommand(customer, new DateOnly(2026, 3, 1), 140m, "wire", [new Allocation(invoice, 100m)]));
        Assert.Equal(40m, await service.GetCustomerCreditBalanceAsync(clientId, customer));

        Refund refund = await service.RecordRefundAsync(clientId, new RefundBody(customer, new DateOnly(2026, 3, 2), 40m, "returned"));

        Assert.Equal(0m, await service.GetCustomerCreditBalanceAsync(clientId, customer));
        Assert.Equal("Refund", ledger.LastPosted!.SourceType);
        Assert.Equal(refund.Id, ledger.LastPosted!.SourceRef);
    }

    [Fact]
    public async Task Refund_exceeding_available_credit_is_rejected()
    {
        (PaymentService service, _, Guid clientId, Guid customer, _) = await SetupIssuedInvoiceAsync(total: 100m);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RecordRefundAsync(clientId, new RefundBody(customer, new DateOnly(2026, 3, 2), 25m, null)));  // no credit exists
    }

    [Fact]
    public async Task Void_refund_restores_credit_balance()
    {
        (PaymentService service, _, Guid clientId, Guid customer, Guid invoice) = await SetupIssuedInvoiceAsync(total: 100m);
        await service.RecordPaymentAsync(clientId,
            new PaymentCommand(customer, new DateOnly(2026, 3, 1), 140m, "wire", [new Allocation(invoice, 100m)]));
        Refund refund = await service.RecordRefundAsync(clientId, new RefundBody(customer, new DateOnly(2026, 3, 2), 40m, null));
        Assert.Equal(0m, await service.GetCustomerCreditBalanceAsync(clientId, customer));

        await service.VoidRefundAsync(clientId, refund.Id, "reissued");

        Assert.Equal(40m, await service.GetCustomerCreditBalanceAsync(clientId, customer));
    }

    // ── Posted→Reverse void branch coverage ─────────────────────────────────

    [Fact]
    public async Task Void_posted_write_off_triggers_reversal()
    {
        // Arrange: record write-off → approve its entry → flip to Posted branch.
        (PaymentService service, FakeLedgerClient ledger, Guid clientId, Guid customer, Guid invoice) =
            await SetupIssuedInvoiceAsync(total: 250m);
        WriteOff wo = await service.RecordWriteOffAsync(clientId,
            new WriteOffCommand(customer, new DateOnly(2026, 3, 1), [new Allocation(invoice, 250m)], null));
        IReadOnlyList<EntryResponse> entries = await ledger.GetEntriesBySourceRefAsync(clientId, wo.Id);
        EntryResponse active = entries.Single(e => e.Status == "Active" && e.ReversalOf == null);
        await ledger.ApproveAsync(clientId, active.Id); // flip to Posted

        // Act: void the write-off — must take the Posted→ReverseAsync branch.
        int reversalsBefore = ledger.ReversalCount;
        await service.VoidWriteOffAsync(clientId, wo.Id, "keyed in error");

        // Assert: ReverseAsync was called (not VoidAsync), and the open balance is restored.
        Assert.Equal(reversalsBefore + 1, ledger.ReversalCount);
        Assert.Equal(250m, (await service.GetInvoiceViewAsync(clientId, invoice))!.OpenBalance);
    }

    [Fact]
    public async Task Void_posted_credit_note_triggers_reversal()
    {
        // Arrange: record credit note → approve its entry → flip to Posted branch.
        (PaymentService service, FakeLedgerClient ledger, Guid clientId, Guid customer, Guid invoice) =
            await SetupIssuedInvoiceAsync(total: 100m);
        CreditNote cn = await service.RecordCreditNoteAsync(clientId,
            new CreditNoteCommand(customer, new DateOnly(2026, 3, 1), [new Allocation(invoice, 40m)], null));
        IReadOnlyList<EntryResponse> entries = await ledger.GetEntriesBySourceRefAsync(clientId, cn.Id);
        EntryResponse active = entries.Single(e => e.Status == "Active" && e.ReversalOf == null);
        await ledger.ApproveAsync(clientId, active.Id); // flip to Posted

        // Act: void the credit note — must take the Posted→ReverseAsync branch.
        int reversalsBefore = ledger.ReversalCount;
        await service.VoidCreditNoteAsync(clientId, cn.Id, "issued in error");

        // Assert: ReverseAsync was called (not VoidAsync), and the open balance is restored.
        Assert.Equal(reversalsBefore + 1, ledger.ReversalCount);
        Assert.Equal(100m, (await service.GetInvoiceViewAsync(clientId, invoice))!.OpenBalance);
    }

    [Fact]
    public async Task Void_posted_refund_triggers_reversal()
    {
        // Arrange: overpay to create credit, record refund, approve its entry → flip to Posted branch.
        (PaymentService service, FakeLedgerClient ledger, Guid clientId, Guid customer, Guid invoice) =
            await SetupIssuedInvoiceAsync(total: 100m);
        await service.RecordPaymentAsync(clientId,
            new PaymentCommand(customer, new DateOnly(2026, 3, 1), 140m, "wire", [new Allocation(invoice, 100m)]));
        Refund refund = await service.RecordRefundAsync(clientId,
            new RefundBody(customer, new DateOnly(2026, 3, 2), 40m, null));
        IReadOnlyList<EntryResponse> entries = await ledger.GetEntriesBySourceRefAsync(clientId, refund.Id);
        EntryResponse active = entries.Single(e => e.Status == "Active" && e.ReversalOf == null);
        await ledger.ApproveAsync(clientId, active.Id); // flip to Posted

        // Act: void the refund — must take the Posted→ReverseAsync branch.
        int reversalsBefore = ledger.ReversalCount;
        await service.VoidRefundAsync(clientId, refund.Id, "reissued");

        // Assert: ReverseAsync was called (not VoidAsync), and the credit balance is restored.
        Assert.Equal(reversalsBefore + 1, ledger.ReversalCount);
        Assert.Equal(40m, await service.GetCustomerCreditBalanceAsync(clientId, customer));
    }

    // ── negative-credit guard on payment void ───────────────────────────────

    private async Task<Invoice> IssueAnotherInvoiceAsync(Harness h, Guid clientId, Guid customerId, decimal total)
    {
        Invoice draft = await h.Invoices.CreateDraftAsync(clientId, new InvoiceBody(
            customerId, new DateOnly(2026, 3, 1), null, 0m, null, [new LineBody("Services", 1m, total, false)]));
        Invoice issued = await h.Invoices.PromoteDraftAsync(clientId, draft.Id);
        PostInvoiceArDebit(h.Ledger, customerId, issued);
        return issued;
    }

    [Fact]
    public async Task Voiding_a_payment_whose_credit_was_applied_is_rejected()
    {
        (Harness h, Guid clientId, Guid customerId, Invoice invoice1) = await SetupWithIssuedInvoiceAsync(100m);
        // Overpay invoice1 by 50 → $50 customer credit.
        Payment pay = await h.Service.RecordPaymentAsync(clientId,
            new PaymentCommand(customerId, new DateOnly(2026, 3, 31), 150m, null, [new Allocation(invoice1.Id, 100m)]));
        // Apply that $50 credit to a second invoice → pool now 0.
        Invoice invoice2 = await IssueAnotherInvoiceAsync(h, clientId, customerId, 100m);
        await h.Service.RecordCreditApplicationAsync(clientId,
            new CreditApplicationCommand(customerId, new DateOnly(2026, 4, 1), [new Allocation(invoice2.Id, 50m)]));

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Service.VoidPaymentAsync(clientId, pay.Id));
        Assert.Contains("already been applied", ex.Message, StringComparison.OrdinalIgnoreCase);

        // The credit balance never went negative; the payment is still active.
        Assert.Equal(0m, await h.Service.GetCustomerCreditBalanceAsync(clientId, customerId));
    }

    [Fact]
    public async Task Voiding_a_payment_whose_credit_is_still_available_succeeds()
    {
        (Harness h, Guid clientId, Guid customerId, Invoice invoice1) = await SetupWithIssuedInvoiceAsync(100m);
        Payment pay = await h.Service.RecordPaymentAsync(clientId,
            new PaymentCommand(customerId, new DateOnly(2026, 3, 31), 150m, null, [new Allocation(invoice1.Id, 100m)]));

        Payment voided = await h.Service.VoidPaymentAsync(clientId, pay.Id);

        Assert.True(voided.Voided);
        Assert.Equal(0m, await h.Service.GetCustomerCreditBalanceAsync(clientId, customerId));
    }

    [Fact]
    public async Task Voiding_one_overpayment_is_allowed_when_other_credit_covers_the_spend()
    {
        (Harness h, Guid clientId, Guid customerId, Invoice invoice1) = await SetupWithIssuedInvoiceAsync(100m);
        // Two overpayments → pool $100.
        Payment payA = await h.Service.RecordPaymentAsync(clientId,
            new PaymentCommand(customerId, new DateOnly(2026, 3, 31), 150m, null, [new Allocation(invoice1.Id, 100m)]));
        Invoice invoice2 = await IssueAnotherInvoiceAsync(h, clientId, customerId, 100m);
        await h.Service.RecordPaymentAsync(clientId,
            new PaymentCommand(customerId, new DateOnly(2026, 3, 31), 150m, null, [new Allocation(invoice2.Id, 100m)]));
        // Spend $50 of credit on a third invoice → pool $50 remains.
        Invoice invoice3 = await IssueAnotherInvoiceAsync(h, clientId, customerId, 100m);
        await h.Service.RecordCreditApplicationAsync(clientId,
            new CreditApplicationCommand(customerId, new DateOnly(2026, 4, 1), [new Allocation(invoice3.Id, 50m)]));

        // Voiding payA ($50 unapplied) is allowed: pool ($50) still covers it (payB's credit absorbs the spend).
        Payment voided = await h.Service.VoidPaymentAsync(clientId, payA.Id);
        Assert.True(voided.Voided);
        Assert.Equal(0m, await h.Service.GetCustomerCreditBalanceAsync(clientId, customerId));
    }
}

internal sealed class FixedPaymentAccountsProvider(PaymentPostingAccounts accounts) : IPaymentAccountsProvider
{
    public Task<PaymentPostingAccounts> GetAsync(Guid clientId, CancellationToken ct = default) => Task.FromResult(accounts);
}
