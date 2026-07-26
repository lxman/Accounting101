using Accounting101.Ledger.Contracts;
using Accounting101.Settlement;
using Accounting101.TestSupport;

namespace Accounting101.Payables.Tests;

/// <summary>
/// The TOCTOU race on a retry key, pinned deterministically.
///
/// <c>BillPaymentService</c> reads the document before deciding created-vs-replayed, which is
/// time-of-check-to-time-of-use: two callers racing one key can both see "not found". The store's
/// atomic create is the real arbiter, and the service must trust it over its own pre-check.
///
/// WHY THIS TEST EXISTS AND WHAT MAKES IT VALID: revert
/// <c>replayed = !result.Created;</c> to <c>replayed = known is not null;</c> in
/// <c>BillPaymentService.RecordPaymentAsync</c> and both racers claim a create, so
/// <c>Assert.Single(..., r =&gt; !r.Replayed)</c> sees two and fails. Its sibling
/// <c>BillPaymentRetryKeyDepthTests.Two_concurrent_posts_with_the_same_key_yield_one_document_and_one_entry</c>
/// does NOT have that property — two real HTTP posts usually serialize, so the second one's pre-check
/// finds the document and returns 200 and that test passes even against the broken shape.
/// </summary>
public sealed class BillPaymentRaceTests
{
    private static readonly BillPaymentPostingAccounts PayAccounts = new()
    {
        PayableAccountId = Guid.NewGuid(), CashAccountId = Guid.NewGuid(), VendorCreditsAccountId = Guid.NewGuid(),
    };

    private static readonly Guid DummyExpenseAccountId = Guid.NewGuid();

    [Fact]
    public async Task Two_racers_past_the_probe_yield_exactly_one_create()
    {
        Guid clientId = Guid.NewGuid();
        Guid vendorId = Guid.NewGuid();

        InMemoryBillStore billStore = new();
        Bill draft = await billStore.CreateDraftAsync(clientId, new BillBody(
            vendorId, new DateOnly(2026, 3, 1), null, null, null,
            [new BillLineBody("Rent", 1000m, Guid.NewGuid())]));
        Bill bill = await billStore.PromoteDraftAsync(clientId, draft.Id);

        // Seed the bill's own A/P credit — the open-balance fold reads the ledger, and this harness
        // bypasses BillService, which is what posts that line in production.
        FakeLedgerClient ledger = new();
        ledger.SeedEntry(new PostEntryRequest(
            Id: null, EffectiveDate: bill.BillDate, Reference: bill.Number, Memo: null,
            Lines:
            [
                new PostLineRequest(DummyExpenseAccountId, "Debit", bill.Total),
                new PostLineRequest(PayAccounts.PayableAccountId, "Credit", bill.Total,
                    Dimensions: new Dictionary<string, Guid> { ["Vendor"] = vendorId, ["Bill"] = bill.Id }),
            ],
            SourceRef: bill.Id, SourceType: "Bill"));

        InMemoryBillPaymentStore inner = new();
        RaceRendezvous rendezvous = new(participants: 2);
        RendezvousBillPaymentStore racingStore = new(inner, rendezvous);
        BillPaymentService service = new(
            racingStore, billStore,
            new FixedBillAccountsProvider(new BillPostingAccounts { PayableAccountId = PayAccounts.PayableAccountId }, PayAccounts),
            ledger);

        // 100 against a 1000 bill, deliberately non-binding: validation runs after the probe and before
        // the create and folds pending-inclusive, so a binding amount would hand the second racer a
        // spurious 422 for a defect this test is not about (retry-keys follow-up #3).
        Guid key = Guid.NewGuid();
        BillPaymentCommand command = new(vendorId, new DateOnly(2026, 3, 31), 100m, "check",
            [new Allocation(bill.Id, 100m)], key);

        Task<(BillPayment Payment, bool Replayed)> first = service.RecordPaymentAsync(clientId, command);
        Task<(BillPayment Payment, bool Replayed)> second = service.RecordPaymentAsync(clientId, command);
        (BillPayment Payment, bool Replayed)[] results = await Task.WhenAll(first, second);

        // THE assertion. Both racers were forced past the probe seeing "not found"; only the atomic
        // create can distinguish them, so exactly one may claim to have created.
        Assert.Single(results, r => !r.Replayed);
        Assert.Single(results, r => r.Replayed);

        // Both racers get back the same persisted document — the loser gets the winner's, not its own body.
        Assert.Equal(key, results[0].Payment.Id);
        Assert.Equal(key, results[1].Payment.Id);
        Assert.Single(await inner.GetPaymentsByVendorAsync(clientId, vendorId));
    }
}
