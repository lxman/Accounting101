using Accounting101.Ledger.Contracts;
using Accounting101.Settlement;
using Accounting101.TestSupport;

namespace Accounting101.Receivables.Tests;

/// <summary>
/// The TOCTOU race on a retry key, pinned deterministically.
///
/// WHAT MAKES THESE VALID: delete the service's <c>replayed = !result.Created;</c> line (leaving
/// <c>replayed</c> at its earlier, pre-check-derived value) and both racers claim a create, so the
/// single-create assertion sees two and fails.
/// </summary>
public sealed class PaymentRaceTests
{
    private static readonly PaymentPostingAccounts Accounts = new()
    {
        ReceivableAccountId = Guid.NewGuid(), CashAccountId = Guid.NewGuid(), CustomerCreditsAccountId = Guid.NewGuid(),
        BadDebtExpenseAccountId = Guid.NewGuid(), SalesReturnsAccountId = Guid.NewGuid(),
    };

    private static readonly Guid DummyRevenueAccountId = Guid.NewGuid();

    /// <summary>Issues a large invoice into <paramref name="invoices"/> and seeds its own A/R debit line
    /// directly into the ledger (mirroring what InvoiceService posts in production) so the fold
    /// PaymentService validates against starts from the correct open balance — this harness bypasses
    /// InvoiceService entirely.</summary>
    private static async Task<Invoice> IssueLargeInvoiceAsync(
        InMemoryInvoiceStore invoices, FakeLedgerClient ledger, Guid clientId, Guid customerId, decimal total)
    {
        Invoice draft = await invoices.CreateDraftAsync(clientId, new InvoiceBody(
            customerId, new DateOnly(2026, 3, 1), null, 0m, null,
            [new LineBody("Services", 1m, total, false)]));
        Invoice issued = await invoices.PromoteDraftAsync(clientId, draft.Id);

        ledger.SeedEntry(new PostEntryRequest(
            Id: null, EffectiveDate: issued.IssueDate, Reference: issued.Number, Memo: null,
            Lines:
            [
                new PostLineRequest(Accounts.ReceivableAccountId, "Debit", issued.Total,
                    Dimensions: new Dictionary<string, Guid> { ["Customer"] = customerId, ["Invoice"] = issued.Id }),
                new PostLineRequest(DummyRevenueAccountId, "Credit", issued.Total),
            ],
            SourceRef: issued.Id, SourceType: "Invoice"));

        return issued;
    }

    [Fact]
    public async Task Two_payment_racers_past_the_probe_yield_exactly_one_create()
    {
        InMemoryPaymentStore inner = new();
        RaceRendezvous rendezvous = new(participants: 2);
        RendezvousPaymentStore racingStore = new(inner, rendezvous);

        Guid clientId = Guid.NewGuid();
        Guid customerId = Guid.NewGuid();
        Guid key = Guid.NewGuid();

        FakeLedgerClient ledger = new();
        InMemoryInvoiceStore invoices = new();
        // A large invoice so a small allocation from each racer is NON-BINDING against the open balance —
        // validation runs after the probe and before the create and folds pending-inclusive, so a binding
        // amount would hand the second racer a spurious 422 for a defect this test is not about
        // (retry-keys follow-up #3).
        Invoice invoice = await IssueLargeInvoiceAsync(invoices, ledger, clientId, customerId, total: 100_000m);

        PaymentService service = new(racingStore, invoices, new FixedPaymentAccountsProvider(Accounts), ledger);

        PaymentCommand command = new(customerId, new DateOnly(2026, 3, 31), 10m, "check", [new Allocation(invoice.Id, 1m)], key);

        Task<(Payment Payment, bool Replayed)> first = service.RecordPaymentAsync(clientId, command);
        Task<(Payment Payment, bool Replayed)> second = service.RecordPaymentAsync(clientId, command);
        (Payment Payment, bool Replayed)[] results = await Task.WhenAll(first, second);

        Assert.Single(results, r => !r.Replayed);
        Assert.Single(results, r => r.Replayed);
        Assert.Equal(key, results[0].Payment.Id);
    }

    [Fact]
    public async Task Two_credit_application_racers_past_the_probe_yield_exactly_one_create()
    {
        InMemoryPaymentStore inner = new();
        RaceRendezvous rendezvous = new(participants: 2);
        RendezvousPaymentStore racingStore = new(inner, rendezvous);

        Guid clientId = Guid.NewGuid();
        Guid customerId = Guid.NewGuid();
        Guid key = Guid.NewGuid();

        FakeLedgerClient ledger = new();
        InMemoryInvoiceStore invoices = new();
        Invoice invoice = await IssueLargeInvoiceAsync(invoices, ledger, clientId, customerId, total: 100_000m);

        // Seed available customer credit well above what the racers apply, so the credit-balance check
        // (state-dependent, run before the create) cannot be what fails. Customer Credits is
        // credit-normal (see PaymentService.GetCustomerCreditBalanceAsync's negate-to-present comment),
        // so a CREDIT line here raises the available balance.
        ledger.SeedEntry(new PostEntryRequest(
            Id: null, EffectiveDate: new DateOnly(2026, 3, 1), Reference: "seed-credit", Memo: null,
            Lines:
            [
                new PostLineRequest(Accounts.CustomerCreditsAccountId, "Credit", 1_000m,
                    Dimensions: new Dictionary<string, Guid> { ["Customer"] = customerId }),
                new PostLineRequest(DummyRevenueAccountId, "Debit", 1_000m),
            ],
            SourceRef: Guid.NewGuid(), SourceType: "CustomerCredit"));

        PaymentService service = new(racingStore, invoices, new FixedPaymentAccountsProvider(Accounts), ledger);

        // Same non-binding rule as the payment race: a small allocation against a large invoice.
        CreditApplicationCommand command = new(customerId, new DateOnly(2026, 3, 2), [new Allocation(invoice.Id, 1m)], key);

        Task<(CreditApplication Application, bool Replayed)> first = service.RecordCreditApplicationAsync(clientId, command);
        Task<(CreditApplication Application, bool Replayed)> second = service.RecordCreditApplicationAsync(clientId, command);
        (CreditApplication Application, bool Replayed)[] results = await Task.WhenAll(first, second);

        Assert.Single(results, r => !r.Replayed);
        Assert.Single(results, r => r.Replayed);
        Assert.Equal(key, results[0].Application.Id);
    }

    [Fact]
    public async Task Two_write_off_racers_past_the_probe_yield_exactly_one_create()
    {
        InMemoryPaymentStore inner = new();
        RaceRendezvous rendezvous = new(participants: 2);
        RendezvousPaymentStore racingStore = new(inner, rendezvous);

        Guid clientId = Guid.NewGuid();
        Guid customerId = Guid.NewGuid();
        Guid key = Guid.NewGuid();

        FakeLedgerClient ledger = new();
        InMemoryInvoiceStore invoices = new();
        // Same non-binding rule as the payment race above: a small allocation against a large invoice.
        Invoice invoice = await IssueLargeInvoiceAsync(invoices, ledger, clientId, customerId, total: 100_000m);

        PaymentService service = new(racingStore, invoices, new FixedPaymentAccountsProvider(Accounts), ledger);

        WriteOffCommand command = new(customerId, new DateOnly(2026, 3, 1), [new Allocation(invoice.Id, 1m)], "uncollectible", key);

        Task<(WriteOff WriteOff, bool Replayed)> first = service.RecordWriteOffAsync(clientId, command);
        Task<(WriteOff WriteOff, bool Replayed)> second = service.RecordWriteOffAsync(clientId, command);
        (WriteOff WriteOff, bool Replayed)[] results = await Task.WhenAll(first, second);

        Assert.Single(results, r => !r.Replayed);
        Assert.Single(results, r => r.Replayed);
        Assert.Equal(key, results[0].WriteOff.Id);
    }

    [Fact]
    public async Task Two_credit_note_racers_past_the_probe_yield_exactly_one_create()
    {
        InMemoryPaymentStore inner = new();
        RaceRendezvous rendezvous = new(participants: 2);
        RendezvousPaymentStore racingStore = new(inner, rendezvous);

        Guid clientId = Guid.NewGuid();
        Guid customerId = Guid.NewGuid();
        Guid key = Guid.NewGuid();

        FakeLedgerClient ledger = new();
        InMemoryInvoiceStore invoices = new();
        // Same non-binding rule as the payment race above: a small allocation against a large invoice.
        Invoice invoice = await IssueLargeInvoiceAsync(invoices, ledger, clientId, customerId, total: 100_000m);

        PaymentService service = new(racingStore, invoices, new FixedPaymentAccountsProvider(Accounts), ledger);

        CreditNoteCommand command = new(customerId, new DateOnly(2026, 3, 1), [new Allocation(invoice.Id, 1m)], "partial return", key);

        Task<(CreditNote CreditNote, bool Replayed)> first = service.RecordCreditNoteAsync(clientId, command);
        Task<(CreditNote CreditNote, bool Replayed)> second = service.RecordCreditNoteAsync(clientId, command);
        (CreditNote CreditNote, bool Replayed)[] results = await Task.WhenAll(first, second);

        Assert.Single(results, r => !r.Replayed);
        Assert.Single(results, r => r.Replayed);
        Assert.Equal(key, results[0].CreditNote.Id);
    }

    [Fact]
    public async Task Two_refund_racers_past_the_probe_yield_exactly_one_create()
    {
        InMemoryPaymentStore inner = new();
        RaceRendezvous rendezvous = new(participants: 2);
        RendezvousPaymentStore racingStore = new(inner, rendezvous);

        Guid clientId = Guid.NewGuid();
        Guid customerId = Guid.NewGuid();
        Guid key = Guid.NewGuid();

        FakeLedgerClient ledger = new();
        InMemoryInvoiceStore invoices = new();

        // A refund needs available customer credit, not an invoice — seed credit well above what the
        // racers refund, same non-binding rule as the credit-application race above (Customer Credits is
        // credit-normal, so a CREDIT line here raises the available balance).
        ledger.SeedEntry(new PostEntryRequest(
            Id: null, EffectiveDate: new DateOnly(2026, 3, 1), Reference: "seed-credit", Memo: null,
            Lines:
            [
                new PostLineRequest(Accounts.CustomerCreditsAccountId, "Credit", 100_000m,
                    Dimensions: new Dictionary<string, Guid> { ["Customer"] = customerId }),
                new PostLineRequest(DummyRevenueAccountId, "Debit", 100_000m),
            ],
            SourceRef: Guid.NewGuid(), SourceType: "CustomerCredit"));

        PaymentService service = new(racingStore, invoices, new FixedPaymentAccountsProvider(Accounts), ledger);

        // PaymentService.RecordRefundAsync takes a RefundBody plus an optional retry key (there is no
        // RefundCommand) — command here is the body, and key is passed explicitly as the retry key.
        RefundBody command = new(customerId, new DateOnly(2026, 3, 2), 1m, "returned");

        Task<(Refund Refund, bool Replayed)> first = service.RecordRefundAsync(clientId, command, key);
        Task<(Refund Refund, bool Replayed)> second = service.RecordRefundAsync(clientId, command, key);
        (Refund Refund, bool Replayed)[] results = await Task.WhenAll(first, second);

        Assert.Single(results, r => !r.Replayed);
        Assert.Single(results, r => r.Replayed);
        Assert.Equal(key, results[0].Refund.Id);
    }
}
