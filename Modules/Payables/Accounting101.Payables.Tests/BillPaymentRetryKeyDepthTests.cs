using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;
using Accounting101.Payables.Api;
using Accounting101.Settlement;
using static Accounting101.Payables.Tests.BillSettlementScenario;

namespace Accounting101.Payables.Tests;

/// <summary>The retry-key behaviours that are properties of the mechanism rather than of any one
/// endpoint: orphan repair, replay against a voided document, concurrency, and the promise that a
/// replay burns no gapless sequence number.</summary>
public sealed class BillPaymentRetryKeyDepthTests(PayablesHostFixture httpFixture, PayablesDocumentStoreFixture docStoreFixture)
    : IClassFixture<PayablesHostFixture>, IClassFixture<PayablesDocumentStoreFixture>
{
    private static readonly BillPaymentPostingAccounts PayAccounts = new()
    {
        PayableAccountId = Guid.NewGuid(), CashAccountId = Guid.NewGuid(), VendorCreditsAccountId = Guid.NewGuid(),
    };

    /// <summary>An entered bill for a fresh vendor, backed by the in-memory bill store — no ledger call
    /// needed to enter it (unlike the HTTP-backed tests, which go through the real BillService/entries).</summary>
    private static async Task<(InMemoryBillStore BillStore, Guid VendorId, Bill Bill)> EnteredBillAsync(Guid clientId, decimal total)
    {
        Guid vendorId = Guid.NewGuid();
        InMemoryBillStore billStore = new();
        Bill draft = await billStore.CreateDraftAsync(clientId, new BillBody(vendorId, new DateOnly(2026, 3, 1), null, null, null,
            [new BillLineBody("Rent", total, Guid.NewGuid())]));
        Bill entered = await billStore.PromoteDraftAsync(clientId, draft.Id);
        return (billStore, vendorId, entered);
    }

    /// <summary>
    /// BillPaymentService folds every open balance from the ledger (<c>ILedgerClient.GetSubledgerAsync</c>),
    /// never from a module-stored amount. This harness's bills are created directly through
    /// <see cref="InMemoryBillStore"/> — bypassing BillService, which is what posts a bill's own
    /// A/P-credit line in production — so the fold would see only the relief-side debit lines
    /// BillPaymentService posts, never the bill's own credit. Seed that credit here so the fold starts
    /// from the correct open balance, mirroring <c>BillPaymentServiceTests.PostBillApCredit</c>.
    /// </summary>
    private static void PostBillApCredit(FakeLedgerClient ledger, Guid vendorId, Bill bill) =>
        ledger.SeedEntry(new PostEntryRequest(
            Id: null, EffectiveDate: bill.BillDate, Reference: bill.Number, Memo: null,
            Lines:
            [
                new PostLineRequest(Guid.NewGuid(), "Debit", bill.Total),
                new PostLineRequest(PayAccounts.PayableAccountId, "Credit", bill.Total,
                    Dimensions: new Dictionary<string, Guid> { ["Vendor"] = vendorId, ["Bill"] = bill.Id }),
            ],
            SourceRef: bill.Id, SourceType: "Bill"));

    [Fact]
    public async Task A_retry_after_a_failed_ledger_post_completes_the_entry_and_leaves_one_document()
    {
        Guid clientId = Guid.NewGuid();
        (InMemoryBillStore billStore, Guid vendorId, Bill bill) = await EnteredBillAsync(clientId, 100m);

        InMemoryBillPaymentStore paymentStore = new();
        FakeLedgerClient innerLedger = new();
        FirstPostFailsLedgerClient ledger = new(innerLedger);
        BillPaymentService service = new(paymentStore, billStore,
            new FixedBillAccountsProvider(new BillPostingAccounts { PayableAccountId = PayAccounts.PayableAccountId }, PayAccounts),
            ledger);

        Guid key = Guid.NewGuid();
        BillPaymentCommand command = new(vendorId, new DateOnly(2026, 3, 31), 100m, "check", [new Allocation(bill.Id, 100m)], key);

        // First attempt: the document gets created and finalized (RecordPaymentAsync runs before the
        // ledger post), then the ledger post itself throws — an orphan document with no entry.
        await Assert.ThrowsAsync<Accounting101.ModuleKit.LedgerClientException>(() => service.RecordPaymentAsync(clientId, command));
        Assert.Equal(1, ledger.PostAttempts);
        Assert.Empty(innerLedger.Posted); // the failed attempt never actually landed

        // Retry with the identical command/key: the create branch is skipped (known != null), but the
        // ledger post is composed and attempted again from the request — this time it succeeds, completing
        // the entry the first attempt never landed.
        (BillPayment payment, bool replayed) = await service.RecordPaymentAsync(clientId, command);

        Assert.True(replayed);
        Assert.Equal(2, ledger.PostAttempts);
        Assert.Single(innerLedger.Posted); // exactly one post ever actually succeeded

        IReadOnlyList<BillPayment> allPayments = await paymentStore.GetPaymentsByVendorAsync(clientId, vendorId);
        Assert.Single(allPayments); // exactly one document under this id, not two

        IReadOnlyList<EntryResponse> entries = await innerLedger.GetEntriesBySourceRefAsync(clientId, key);
        EntryResponse entry = Assert.Single(entries, e => e.ReversalOf is null);
        Assert.Equal(payment.Id, entry.SourceRef);
    }

    [Fact]
    public async Task A_replayed_key_on_a_voided_payment_returns_the_voided_payment_and_creates_nothing()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await httpFixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId, httpFixture);
        Guid vendor = await CreateVendorAsync(clerk, clientId);
        Guid bill = await EnterBillAsync(clerk, approver, clientId, vendor, 100m, httpFixture.RentExpenseAccountId);

        Guid key = Guid.NewGuid();
        RecordBillPaymentRequest req = new(vendor, new DateOnly(2026, 3, 1), 100m, "check", [new Allocation(bill, 100m)], key);

        BillPayment recorded = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments", req))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<BillPayment>())!;
        Assert.False(recorded.Voided);

        (await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments/{recorded.Id}/void", new VoidReasonRequest("duplicate entry")))
            .EnsureSuccessStatusCode();

        // Re-POST the identical key + identical content against the now-voided payment.
        HttpResponseMessage replay = await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments", req);

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode); // a replay, not a create
        BillPayment replayedPayment = (await replay.Content.ReadFromJsonAsync<BillPayment>())!;
        Assert.Equal(recorded.Id, replayedPayment.Id);
        Assert.True(replayedPayment.Voided); // reports what is actually there — does not resurrect it

        BillPayment[] all = (await clerk.GetFromJsonAsync<BillPayment[]>($"/clients/{clientId}/bill-payments?vendorId={vendor}"))!;
        Assert.Single(all); // no second document was created
    }

    /// <summary>
    /// End-to-end proof that one document and one non-reversal entry survive two same-key posts, through
    /// real HTTP and real Mongo — coverage the service-level race test cannot provide.
    ///
    /// It does NOT pin the TOCTOU fix: these two posts usually serialize, and when they do the second
    /// one's pre-check finds the document and returns 200, so the status assertions below hold even if
    /// the fix is reverted. <see cref="BillPaymentRaceTests"/> is what forces the interleaving and fails
    /// when the fix is absent. Keep both.
    /// </summary>
    [Fact]
    public async Task Two_concurrent_posts_with_the_same_key_yield_one_document_and_one_entry()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await httpFixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId, httpFixture);
        Guid vendor = await CreateVendorAsync(clerk, clientId);
        Guid bill = await EnterBillAsync(clerk, approver, clientId, vendor, 100m, httpFixture.RentExpenseAccountId);

        Guid key = Guid.NewGuid();
        RecordBillPaymentRequest req = new(vendor, new DateOnly(2026, 3, 1), 100m, "check", [new Allocation(bill, 100m)], key);

        Task<HttpResponseMessage> first = clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments", req);
        Task<HttpResponseMessage> second = clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments", req);
        HttpResponseMessage[] responses = await Task.WhenAll(first, second);

        // Order-independent: exactly one of the two racing posts created, the other replayed.
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);

        BillPayment[] all = (await clerk.GetFromJsonAsync<BillPayment[]>($"/clients/{clientId}/bill-payments?vendorId={vendor}"))!;
        Assert.Single(all); // exactly one document, whichever attempt actually created it

        EntryResponse[] entries = (await clerk.GetFromJsonAsync<EntryResponse[]>($"/clients/{clientId}/entries?sourceRef={key}"))!;
        Assert.Single(entries, e => e.ReversalOf is null); // exactly one non-reversal entry landed
    }

    [Fact]
    public async Task A_replay_burns_no_gapless_sequence_number()
    {
        Guid clientId = docStoreFixture.ClientId;
        Guid vendorId = Guid.NewGuid();
        InMemoryBillStore billStore = new();
        Bill draft = await billStore.CreateDraftAsync(clientId, new BillBody(vendorId, new DateOnly(2026, 3, 1), null, null, null,
            [new BillLineBody("Rent", 100m, Guid.NewGuid())]));
        Bill bill = await billStore.PromoteDraftAsync(clientId, draft.Id);

        // BillPayment carries no Number (confirmed against BillPayment.cs — it has Id, VendorId, Date,
        // Amount, Method, Voided, and nothing else; DocumentBillPaymentStore.MapPayment maps no sequence
        // into it either). The gapless counter the plan means is IDocumentStore's own Sequence, assigned at
        // FinalizeAsync and readable directly off the real document store — so this test goes around the
        // service's fake store and reads it from the actual engine-backed IDocumentStore.
        FakeLedgerClient ledger = new();
        PostBillApCredit(ledger, vendorId, bill);
        DocumentBillPaymentStore paymentStore = new(docStoreFixture.Store);
        BillPaymentService service = new(paymentStore, billStore,
            new FixedBillAccountsProvider(new BillPostingAccounts { PayableAccountId = PayAccounts.PayableAccountId }, PayAccounts),
            ledger);

        Guid keyA = Guid.NewGuid();
        BillPaymentCommand commandA = new(vendorId, new DateOnly(2026, 3, 1), 40m, "check", [new Allocation(bill.Id, 40m)], keyA);
        BillPayment a = (await service.RecordPaymentAsync(clientId, commandA)).Payment;

        (BillPayment replayOfA, bool replayed) = await service.RecordPaymentAsync(clientId, commandA);
        Assert.True(replayed);
        Assert.Equal(a.Id, replayOfA.Id);

        // Payment B: a fresh (unkeyed) payment against the same bill's remaining balance.
        BillPaymentCommand commandB = new(vendorId, new DateOnly(2026, 3, 2), 20m, "check", [new Allocation(bill.Id, 20m)]);
        BillPayment b = (await service.RecordPaymentAsync(clientId, commandB)).Payment;

        DocumentResult<BillPaymentBody> seqA = (await docStoreFixture.Store.GetAsync<BillPaymentBody>(clientId, "bill-payments", a.Id))!;
        DocumentResult<BillPaymentBody> seqB = (await docStoreFixture.Store.GetAsync<BillPaymentBody>(clientId, "bill-payments", b.Id))!;

        Assert.NotNull(seqA.Sequence);
        Assert.NotNull(seqB.Sequence);
        Assert.Equal(seqA.Sequence!.Value + 1, seqB.Sequence!.Value); // the replay of A consumed no sequence number
    }

    /// <summary>
    /// <see cref="DocumentBillPaymentStore.GetCreditApplicationsByVendorAsync"/> passes
    /// <c>includeVoided: true</c> to the underlying query — a real, user-visible behaviour (the Credits
    /// list greys out voided applications rather than hiding them; see
    /// <c>BillPaymentService.GetCreditApplicationsWithAllocationsByVendorAsync</c>'s own doc comment).
    /// This proves it against the real engine-backed document store, not a fake: record a credit
    /// application, void the underlying document directly (there is no module-level void for a credit
    /// application today — only payments have one, via <c>BillPaymentService.VoidPaymentAsync</c> — so
    /// this goes straight at the same "vendor-credit-applications" collection the store itself writes
    /// to), then confirm it still comes back from the by-vendor list instead of vanishing.
    ///
    /// DISCRIMINATOR: temporarily remove <c>includeVoided: true</c> from
    /// <c>DocumentBillPaymentStore.GetCreditApplicationsByVendorAsync</c> (falling back to the
    /// <c>QueryAsync</c> default of <c>false</c>) and this test goes red — the voided application
    /// disappears from the list instead of appearing greyed. That is the property
    /// <c>BillPaymentRaceTests.Recording_a_credit_application_makes_it_appear_in_the_vendor_list</c>
    /// (the fake-backed sibling) cannot prove: its fake has no void concept at all.
    /// </summary>
    [Fact]
    public async Task A_voided_credit_application_still_appears_in_the_vendor_list()
    {
        Guid clientId = docStoreFixture.ClientId;
        Guid vendorId = Guid.NewGuid();
        DocumentBillPaymentStore store = new(docStoreFixture.Store);

        Guid id = Guid.NewGuid();
        (VendorCreditApplication recorded, bool created) = await store.RecordCreditApplicationAsync(
            clientId, id, new VendorCreditApplicationBody(vendorId, new DateOnly(2026, 3, 1)));
        Assert.True(created);
        Assert.False(recorded.Voided);

        await docStoreFixture.Store.VoidAsync(clientId, "vendor-credit-applications", id);

        IReadOnlyList<VendorCreditApplication> byVendor = await store.GetCreditApplicationsByVendorAsync(clientId, vendorId);
        VendorCreditApplication voided = Assert.Single(byVendor);
        Assert.True(voided.Voided);
    }
}
