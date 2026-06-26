using Accounting101.Ledger.Contracts;
using Accounting101.Settlement;

namespace Accounting101.Payables.Tests;

public sealed class BillPaymentServiceTests
{
    private static readonly BillPaymentPostingAccounts PayAccounts = new()
    {
        PayableAccountId = Guid.NewGuid(), CashAccountId = Guid.NewGuid(), VendorCreditsAccountId = Guid.NewGuid(),
    };

    private sealed record Harness(BillPaymentService Payments, FakeLedgerClient Ledger, InMemoryBillStore BillStore, InMemoryBillPaymentStore PaymentStore);

    private static async Task<(Harness h, Guid clientId, Guid vendorId, Bill bill)> SetupWithEnteredBillAsync(decimal total)
    {
        Guid clientId = Guid.NewGuid();
        Guid vendorId = Guid.NewGuid();
        InMemoryBillStore billStore = new();
        Bill draft = await billStore.CreateDraftAsync(clientId, new BillBody(
            vendorId, new DateOnly(2026, 3, 1), null, null, null,
            [new BillLineBody("Rent", total, Guid.NewGuid())]));
        Bill entered = await billStore.FinalizeAsync(clientId, draft.Id);

        FakeLedgerClient ledger = new();
        InMemoryBillPaymentStore paymentStore = new();
        BillPaymentService service = new(paymentStore, billStore, new FixedBillAccountsProvider(new BillPostingAccounts { PayableAccountId = PayAccounts.PayableAccountId }, PayAccounts), ledger);
        return (new Harness(service, ledger, billStore, paymentStore), clientId, vendorId, entered);
    }

    [Fact]
    public async Task Records_a_payment_and_posts_a_pending_settlement_entry()
    {
        (Harness h, Guid clientId, Guid vendorId, Bill bill) = await SetupWithEnteredBillAsync(100m);

        BillPaymentBody body = new(vendorId, new DateOnly(2026, 3, 31), 40m, null, [new Allocation(bill.Id, 40m)]);
        BillPayment recorded = await h.Payments.RecordPaymentAsync(clientId, body);

        Assert.NotEqual(Guid.Empty, recorded.Id);
        PostEntryRequest entry = Assert.Single(h.Ledger.Posted);
        Assert.Equal("BillPayment", entry.SourceType);
        Assert.Equal(recorded.Id, entry.SourceRef);
    }

    [Fact]
    public async Task Rejects_a_payment_whose_allocations_exceed_its_amount()
    {
        (Harness h, Guid clientId, Guid vendorId, Bill bill) = await SetupWithEnteredBillAsync(100m);

        BillPaymentBody body = new(vendorId, new DateOnly(2026, 3, 31), 40m, null, [new Allocation(bill.Id, 60m)]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Payments.RecordPaymentAsync(clientId, body));
        Assert.Empty(h.Ledger.Posted);
    }

    [Fact]
    public async Task Rejects_an_allocation_exceeding_a_bill_open_balance()
    {
        (Harness h, Guid clientId, Guid vendorId, Bill bill) = await SetupWithEnteredBillAsync(100m);

        BillPaymentBody body = new(vendorId, new DateOnly(2026, 3, 31), 150m, null, [new Allocation(bill.Id, 150m)]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Payments.RecordPaymentAsync(clientId, body));
    }

    [Fact]
    public async Task Bill_view_reflects_a_partial_payment()
    {
        (Harness h, Guid clientId, Guid vendorId, Bill bill) = await SetupWithEnteredBillAsync(100m);
        await h.Payments.RecordPaymentAsync(clientId, new BillPaymentBody(vendorId, new DateOnly(2026, 3, 31), 40m, null, [new Allocation(bill.Id, 40m)]));

        BillView? view = await h.Payments.GetBillViewAsync(clientId, bill.Id);

        Assert.NotNull(view);
        Assert.Equal(60m, view!.OpenBalance);
        Assert.Equal(SettlementStatus.PartiallyPaid, view.SettlementStatus);
    }

    [Fact]
    public async Task Over_payment_raises_the_vendor_credit_balance()
    {
        (Harness h, Guid clientId, Guid vendorId, Bill bill) = await SetupWithEnteredBillAsync(100m);
        await h.Payments.RecordPaymentAsync(clientId, new BillPaymentBody(vendorId, new DateOnly(2026, 3, 31), 150m, null, [new Allocation(bill.Id, 100m)]));

        Assert.Equal(50m, await h.Payments.GetVendorCreditBalanceAsync(clientId, vendorId));
        BillView? view = await h.Payments.GetBillViewAsync(clientId, bill.Id);
        Assert.Equal(SettlementStatus.Paid, view!.SettlementStatus);
    }

    [Fact]
    public async Task Applies_existing_credit_to_a_bill_and_lowers_the_balance()
    {
        (Harness h, Guid clientId, Guid vendorId, Bill first) = await SetupWithEnteredBillAsync(100m);
        // Create $50 of credit via over-payment on the first bill.
        await h.Payments.RecordPaymentAsync(clientId, new BillPaymentBody(vendorId, new DateOnly(2026, 3, 31), 150m, null, [new Allocation(first.Id, 100m)]));
        // A second entered bill to apply credit against.
        Bill draft2 = await h.BillStore.CreateDraftAsync(clientId, new BillBody(vendorId, new DateOnly(2026, 4, 1), null, null, null, [new BillLineBody("More", 100m, Guid.NewGuid())]));
        Bill second = await h.BillStore.FinalizeAsync(clientId, draft2.Id);

        VendorCreditApplication applied = await h.Payments.RecordCreditApplicationAsync(clientId,
            new VendorCreditApplicationBody(vendorId, new DateOnly(2026, 4, 2), [new Allocation(second.Id, 50m)]));

        Assert.Equal(50m, applied.Applied);
        Assert.Equal(0m, await h.Payments.GetVendorCreditBalanceAsync(clientId, vendorId));
        Assert.Equal(50m, (await h.Payments.GetBillViewAsync(clientId, second.Id))!.OpenBalance);
        Assert.Contains(h.Ledger.Posted, e => e.SourceType == "VendorCreditApplication");
    }

    [Fact]
    public async Task Rejects_a_credit_application_exceeding_available_credit()
    {
        (Harness h, Guid clientId, Guid vendorId, Bill bill) = await SetupWithEnteredBillAsync(100m);
        // No credit created yet.
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Payments.RecordCreditApplicationAsync(clientId,
            new VendorCreditApplicationBody(vendorId, new DateOnly(2026, 4, 2), [new Allocation(bill.Id, 25m)])));
    }

    [Fact]
    public async Task Voiding_a_payment_restores_the_bill_open_balance()
    {
        (Harness h, Guid clientId, Guid vendorId, Bill bill) = await SetupWithEnteredBillAsync(100m);
        BillPayment p = await h.Payments.RecordPaymentAsync(clientId, new BillPaymentBody(vendorId, new DateOnly(2026, 3, 31), 40m, null, [new Allocation(bill.Id, 40m)]));
        Assert.Equal(60m, (await h.Payments.GetBillViewAsync(clientId, bill.Id))!.OpenBalance);

        await h.Payments.VoidPaymentAsync(clientId, p.Id);

        Assert.Equal(100m, (await h.Payments.GetBillViewAsync(clientId, bill.Id))!.OpenBalance);
        Assert.Equal(SettlementStatus.Open, (await h.Payments.GetBillViewAsync(clientId, bill.Id))!.SettlementStatus);
    }

    [Fact]
    public async Task Lists_vendor_bills_filtered_by_settlement()
    {
        (Harness h, Guid clientId, Guid vendorId, Bill first) = await SetupWithEnteredBillAsync(100m);
        // Pay the first bill in full -> Paid.
        await h.Payments.RecordPaymentAsync(clientId, new BillPaymentBody(vendorId, new DateOnly(2026, 3, 31), 100m, null, [new Allocation(first.Id, 100m)]));
        // A second, unpaid bill -> Open.
        Bill d2 = await h.BillStore.CreateDraftAsync(clientId, new BillBody(vendorId, new DateOnly(2026, 4, 1), null, null, null, [new BillLineBody("More", 100m, Guid.NewGuid())]));
        Bill second = await h.BillStore.FinalizeAsync(clientId, d2.Id);

        IReadOnlyList<BillView> open = await h.Payments.ListBillViewsAsync(clientId, vendorId, SettlementFilter.Open);
        IReadOnlyList<BillView> paid = await h.Payments.ListBillViewsAsync(clientId, vendorId, SettlementFilter.Paid);
        IReadOnlyList<BillView> all = await h.Payments.ListBillViewsAsync(clientId, vendorId, null);

        Assert.Equal(second.Id, Assert.Single(open).Bill.Id);
        Assert.Equal(first.Id, Assert.Single(paid).Bill.Id);
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task Voided_bill_does_not_appear_in_list_views()
    {
        // Arrange: enter a bill then void it.
        (Harness h, Guid clientId, Guid vendorId, Bill bill) = await SetupWithEnteredBillAsync(100m);
        await h.BillStore.VoidAsync(clientId, bill.Id);

        // Act: query both the all-bills list and the open-settlement filter.
        IReadOnlyList<BillView> all  = await h.Payments.ListBillViewsAsync(clientId, vendorId, null);
        IReadOnlyList<BillView> open = await h.Payments.ListBillViewsAsync(clientId, vendorId, SettlementFilter.Open);

        // Assert: a voided bill is not a settleable payable — it must not appear in either view.
        Assert.Empty(all);
        Assert.Empty(open);
    }

    // ── settlement-gating: only Entered bills are settleable ────────────────

    [Fact]
    public async Task Draft_bill_does_not_appear_in_list_views()
    {
        // Arrange: create a draft bill (never enter/finalize it).
        Guid clientId = Guid.NewGuid();
        Guid vendorId = Guid.NewGuid();
        InMemoryBillStore billStore = new();
        await billStore.CreateDraftAsync(clientId, new BillBody(
            vendorId, new DateOnly(2026, 3, 1), null, null, null,
            [new BillLineBody("Rent", 100m, Guid.NewGuid())]));

        InMemoryBillPaymentStore paymentStore = new();
        FakeLedgerClient ledger = new();
        BillPaymentService service = new(paymentStore, billStore,
            new FixedBillAccountsProvider(new BillPostingAccounts { PayableAccountId = PayAccounts.PayableAccountId }, PayAccounts), ledger);

        // Act: query both the all-bills list and the open-settlement filter.
        IReadOnlyList<BillView> all  = await service.ListBillViewsAsync(clientId, vendorId, null);
        IReadOnlyList<BillView> open = await service.ListBillViewsAsync(clientId, vendorId, SettlementFilter.Open);

        // Assert: a draft bill must not appear in either settlement view.
        Assert.Empty(all);
        Assert.Empty(open);
    }

    [Fact]
    public async Task Allocating_payment_to_draft_bill_is_rejected_with_entered_message()
    {
        // Arrange: create a draft bill (never finalize it).
        Guid clientId = Guid.NewGuid();
        Guid vendorId = Guid.NewGuid();
        InMemoryBillStore billStore = new();
        Bill draft = await billStore.CreateDraftAsync(clientId, new BillBody(
            vendorId, new DateOnly(2026, 3, 1), null, null, null,
            [new BillLineBody("Rent", 100m, Guid.NewGuid())]));

        InMemoryBillPaymentStore paymentStore = new();
        FakeLedgerClient ledger = new();
        BillPaymentService service = new(paymentStore, billStore,
            new FixedBillAccountsProvider(new BillPostingAccounts { PayableAccountId = PayAccounts.PayableAccountId }, PayAccounts), ledger);

        // Act & Assert: paying a draft bill must throw with a message mentioning "entered".
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RecordPaymentAsync(clientId,
                new BillPaymentBody(vendorId, new DateOnly(2026, 3, 31), 100m, null, [new Allocation(draft.Id, 100m)])));
        Assert.Contains("entered", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Allocating_payment_to_void_bill_is_rejected()
    {
        // Arrange: enter then void.
        (Harness h, Guid clientId, Guid vendorId, Bill bill) = await SetupWithEnteredBillAsync(100m);
        await h.BillStore.VoidAsync(clientId, bill.Id);

        // Act & Assert: paying a void bill must throw.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Payments.RecordPaymentAsync(clientId,
                new BillPaymentBody(vendorId, new DateOnly(2026, 3, 31), 100m, null, [new Allocation(bill.Id, 100m)])));
    }

    [Fact]
    public async Task Allocating_payment_to_entered_bill_succeeds()
    {
        // Arrange: the standard setup already enters the bill.
        (Harness h, Guid clientId, Guid vendorId, Bill bill) = await SetupWithEnteredBillAsync(100m);

        // Act: pay in full — must not throw.
        BillPayment recorded = await h.Payments.RecordPaymentAsync(clientId,
            new BillPaymentBody(vendorId, new DateOnly(2026, 3, 31), 100m, null, [new Allocation(bill.Id, 100m)]));

        // Assert: payment recorded and settled.
        Assert.NotEqual(Guid.Empty, recorded.Id);
        BillView? view = await h.Payments.GetBillViewAsync(clientId, bill.Id);
        Assert.Equal(SettlementStatus.Paid, view!.SettlementStatus);
    }
}
