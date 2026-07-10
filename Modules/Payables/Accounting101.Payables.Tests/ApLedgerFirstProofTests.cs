using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;
using Accounting101.Payables.Api;
using Accounting101.Settlement;
using static Accounting101.Payables.Tests.BillSettlementScenario;

namespace Accounting101.Payables.Tests;

/// <summary>
/// Task 8 — the merge-gate proof suite for the AP ledger-first slice. Codifies the spec's §9 proof
/// obligations as ONE explicit set of end-to-end tests: every fact a caller can observe about a bill's or
/// a vendor's open balance is folded from the ledger (dimension-tagged A/P lines), never read from a
/// module-owned mirror. Every assertion here reads the ledger fold, a
/// <c>/subledger[/reconciliation]</c> response, or a derived view (<see cref="BillView"/>,
/// <see cref="VendorAccountView"/>) that itself folds the ledger — never a module-stored amount
/// (<see cref="BillPayment"/> and <see cref="VendorCreditApplication"/> no longer even carry an allocation
/// array to read).
/// <para>
/// ⭐ Signs differ from AR: A/P is credit-normal (mirror image of AR's debit-normal A/R), so a bill's open
/// balance is <c>open = -fold</c>. Vendor Credits is a debit-normal ASSET (same normal balance as AR's
/// A/R — unlike AR's Customer Credits, a liability, which negates): its fold reads available credit
/// directly POSITIVE, no negation. Both signs are pinned explicitly below so a blind AR copy — negating
/// the wrong side, or failing to negate the right one — fails loudly.
/// </para>
/// <para>
/// Several obligations are also exercised — at the recipe / entry-line level rather than the obligation
/// level — by tests written during Tasks 2-7 (<see cref="BillDimensionTests"/>,
/// <see cref="BillPaymentDimensionTests"/>, <see cref="VendorCreditApplicationDimensionTests"/>,
/// <see cref="FoldReadTests"/>, <see cref="SubledgerReadTests"/>). Where that is true this suite still
/// asserts the obligation directly (reads the fold / reconciliation / view, not raw entry lines), so it is
/// not a pure duplicate; the one true exception is §9.6, which is not restated here at all because
/// <see cref="ApRequiresBillTests.Raw_AP_line_without_Bill_dimension_is_rejected"/> already proves it
/// exactly as an engine-level guarantee, independent of any module recipe.
/// </para>
/// </summary>
public sealed class ApLedgerFirstProofTests(PayablesHostFixture fixture) : IClassFixture<PayablesHostFixture>
{
    private Task SetUpChart(HttpClient controller, Guid clientId) =>
        SetUpChartAsync(controller, clientId, fixture);

    /// <summary>The raw Bill-axis A/P fold for one bill. Defaults to 0 when the dimension carries no
    /// on-the-books line at all (never entered, or its enter entry not yet approved) — the same fallback
    /// used by every other fold helper in this suite family.</summary>
    private async Task<decimal> BillFoldAsync(HttpClient http, Guid clientId, Guid billId)
    {
        SubledgerResponse fold = (await http.GetFromJsonAsync<SubledgerResponse>(
            $"/clients/{clientId}/subledger?account={fixture.PayableAccountId}&dimension=Bill"))!;
        SubledgerLineResponse? line = fold.Lines.SingleOrDefault(l => l.DimensionValue == billId);
        return line?.Balance ?? 0m;
    }

    /// <summary>§9.1 — Bill enter → the bill's Bill-axis fold gives open = full total, sign-correct: A/P is
    /// credit-normal, so open = -fold (the fold itself reads negative the bill total).</summary>
    [Fact]
    public async Task Enter_sets_the_bills_fold_so_open_equals_the_full_total()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChart(controller, clientId);
        Guid vendor = await CreateVendorAsync(clerk, clientId);

        Guid billId = await EnterBillAsync(clerk, approver, clientId, vendor, 100m, fixture.RentExpenseAccountId);

        decimal fold = await BillFoldAsync(clerk, clientId, billId);
        Assert.Equal(-100m, fold);

        BillView view = (await clerk.GetFromJsonAsync<BillView>($"/clients/{clientId}/bills/{billId}"))!;
        Assert.Equal(100m, view.OpenBalance);
        Assert.Equal(view.OpenBalance, -fold);
    }

    /// <summary>§9.2 — An approved partial payment (one allocation) reduces the bill's fold-derived open by
    /// exactly the allocation.</summary>
    [Fact]
    public async Task Approved_partial_payment_reduces_the_bills_open_by_the_allocation()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChart(controller, clientId);
        Guid vendor = await CreateVendorAsync(clerk, clientId);
        Guid billId = await EnterBillAsync(clerk, approver, clientId, vendor, 100m, fixture.RentExpenseAccountId);

        BillPayment payment = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
                new RecordBillPaymentRequest(vendor, new DateOnly(2026, 3, 31), 30m, "check", [new Allocation(billId, 30m)])))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<BillPayment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, payment.Id);

        Assert.Equal(70m, -await BillFoldAsync(clerk, clientId, billId));

        BillView view = (await clerk.GetFromJsonAsync<BillView>($"/clients/{clientId}/bills/{billId}"))!;
        Assert.Equal(70m, view.OpenBalance);
    }

    /// <summary>§9.3 — A split payment across two bills reduces EACH bill's open by its own allocation, not
    /// a shared/aggregate amount. The recipe shape (one Bill-tagged A/P line per allocation) is proven at
    /// the entry-line level by
    /// <see cref="BillPaymentDimensionTests.Split_payment_produces_one_Bill_dimensioned_AP_line_per_allocation"/>;
    /// this test asserts the obligation itself, directly against each bill's derived open balance.</summary>
    [Fact]
    public async Task Split_payment_reduces_each_bills_open_by_its_own_allocation()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChart(controller, clientId);
        Guid vendor = await CreateVendorAsync(clerk, clientId);
        Guid billA = await EnterBillAsync(clerk, approver, clientId, vendor, 100m, fixture.RentExpenseAccountId);
        Guid billB = await EnterBillAsync(clerk, approver, clientId, vendor, 100m, fixture.RentExpenseAccountId);

        BillPayment payment = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
                new RecordBillPaymentRequest(vendor, new DateOnly(2026, 3, 31), 150m, "check",
                    [new Allocation(billA, 100m), new Allocation(billB, 50m)])))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<BillPayment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, payment.Id);

        BillView viewA = (await clerk.GetFromJsonAsync<BillView>($"/clients/{clientId}/bills/{billA}"))!;
        BillView viewB = (await clerk.GetFromJsonAsync<BillView>($"/clients/{clientId}/bills/{billB}"))!;
        Assert.Equal(0m, viewA.OpenBalance);
        Assert.Equal(50m, viewB.OpenBalance);
    }

    /// <summary>§9.4 — The vendor-axis fold equals the sum of that vendor's open bills, and BOTH the
    /// Vendor-axis and Bill-axis reconciliations tie out (now that A/P requires BOTH dimensions, the
    /// Bill-axis reconciliation endpoint no longer 422s — the Task 5 flip that enabled it).</summary>
    [Fact]
    public async Task Vendor_axis_fold_equals_open_bills_and_both_dimensions_tie_out()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChart(controller, clientId);
        Guid vendor = await CreateVendorAsync(clerk, clientId);
        Guid billA = await EnterBillAsync(clerk, approver, clientId, vendor, 100m, fixture.RentExpenseAccountId);
        Guid billB = await EnterBillAsync(clerk, approver, clientId, vendor, 100m, fixture.RentExpenseAccountId);

        BillPayment payment = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
                new RecordBillPaymentRequest(vendor, new DateOnly(2026, 3, 5), 40m, "check", [new Allocation(billA, 40m)])))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<BillPayment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, payment.Id);

        VendorAccountView account = (await clerk.GetFromJsonAsync<VendorAccountView>(
            $"/clients/{clientId}/vendors/{vendor}/account?asOf=2026-04-01"))!;
        decimal sumOfOpenBills = account.OpenBills.Sum(l => l.OpenBalance);
        Assert.Equal(160m, sumOfOpenBills); // (100 - 40) + 100

        SubledgerResponse vendorFold = (await clerk.GetFromJsonAsync<SubledgerResponse>(
            $"/clients/{clientId}/subledger?account={fixture.PayableAccountId}&dimension=Vendor"))!;
        decimal vendorBalance = -vendorFold.Lines.Single(l => l.DimensionValue == vendor).Balance;
        Assert.Equal(sumOfOpenBills, vendorBalance);

        SubledgerReconciliationResponse byVendor = (await clerk.GetFromJsonAsync<SubledgerReconciliationResponse>(
            $"/clients/{clientId}/subledger/reconciliation?account={fixture.PayableAccountId}&dimension=Vendor"))!;
        Assert.True(byVendor.TiesOut);

        SubledgerReconciliationResponse byBill = (await clerk.GetFromJsonAsync<SubledgerReconciliationResponse>(
            $"/clients/{clientId}/subledger/reconciliation?account={fixture.PayableAccountId}&dimension=Bill"))!;
        Assert.True(byBill.TiesOut);
    }

    /// <summary>§9.5 — Over-application is impossible from either angle: (a) a single allocation exceeding
    /// the bill's open balance (read from the fold) is rejected, and the fold is unchanged by the rejected
    /// attempt; (b) two unapplied (unapproved) payments each for the bill's full open balance — the second
    /// is rejected AT RECORD, before either is approved, because validation reserves against
    /// pending-plus-posted reliefs, not just what is already on the books. The rejection messages are also
    /// pinned by <c>BillAllocationBoundaryE2eTests.Allocation_exceeding_open_balance_is_rejected</c> and
    /// <see cref="FoldReadTests.Second_unapproved_overlapping_payment_is_rejected_pending_inclusive"/>; this
    /// test adds the fold-level assertions those don't make.</summary>
    [Fact]
    public async Task Overapplication_is_rejected_against_open_balance_and_against_pending_reliefs()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChart(controller, clientId);
        Guid vendor = await CreateVendorAsync(clerk, clientId);
        Guid billId = await EnterBillAsync(clerk, approver, clientId, vendor, 100m, fixture.RentExpenseAccountId);

        // (a) A single allocation over the bill's whole open balance (100) is rejected outright.
        HttpResponseMessage overResp = await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
            new RecordBillPaymentRequest(vendor, new DateOnly(2026, 3, 5), 150m, "check", [new Allocation(billId, 150m)]));
        await AssertProblemAsync(overResp, HttpStatusCode.UnprocessableEntity, "exceeds its open balance");
        Assert.Equal(-100m, await BillFoldAsync(clerk, clientId, billId)); // untouched by the rejected attempt

        // (b) Payment A: alloc 100, recorded but deliberately left unapproved — nothing hits the books yet,
        // so the Posted-only read fold still shows the bill fully open.
        HttpResponseMessage payAResp = await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
            new RecordBillPaymentRequest(vendor, new DateOnly(2026, 3, 6), 100m, "check", [new Allocation(billId, 100m)]));
        payAResp.EnsureSuccessStatusCode();
        Assert.Equal(-100m, await BillFoldAsync(clerk, clientId, billId));

        // Payment B, also 100 against the same bill while A is still pending, is rejected at record.
        HttpResponseMessage payBResp = await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
            new RecordBillPaymentRequest(vendor, new DateOnly(2026, 3, 7), 100m, "check", [new Allocation(billId, 100m)]));
        await AssertProblemAsync(payBResp, HttpStatusCode.UnprocessableEntity, "exceeds its open balance");
    }

    // §9.6 — a raw A/P line missing the Bill dimension is rejected 422, naming the missing dimension.
    // Already proven, exactly, engine-level, independent of any module recipe, by
    // ApRequiresBillTests.Raw_AP_line_without_Bill_dimension_is_rejected. Not duplicated here.

    /// <summary>§9.7 — A vendor credit application relieving a bill reduces that bill's fold-derived open
    /// by exactly the applied amount. <see cref="VendorCreditApplicationDimensionTests"/> proves the recipe
    /// shape (the raw Bill-tagged A/P line); this test asserts the obligation itself against the bill's
    /// derived <see cref="BillView.OpenBalance"/>.</summary>
    [Fact]
    public async Task Vendor_credit_application_reduces_the_relieved_bills_open_by_the_applied_amount()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChart(controller, clientId);
        Guid vendor = await CreateVendorAsync(clerk, clientId);

        // First bill ($100), overpaid $100 with only $40 allocated to it, leaving a $60 vendor credit.
        Guid firstBill = await EnterBillAsync(clerk, approver, clientId, vendor, 100m, fixture.RentExpenseAccountId);
        BillPayment payment = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
                new RecordBillPaymentRequest(vendor, new DateOnly(2026, 3, 31), 100m, "check", [new Allocation(firstBill, 40m)])))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<BillPayment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, payment.Id);

        // Second bill ($100) — the $60 vendor credit is applied against it.
        Guid secondBill = await EnterBillAsync(clerk, approver, clientId, vendor, 100m, fixture.RentExpenseAccountId);
        VendorCreditApplication creditApp = (await (await clerk.PostAsJsonAsync(
                $"/clients/{clientId}/vendor-credit-applications",
                new VendorCreditApplicationRequest(vendor, new DateOnly(2026, 4, 15), [new Allocation(secondBill, 60m)])))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<VendorCreditApplication>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, creditApp.Id);

        BillView secondView = (await clerk.GetFromJsonAsync<BillView>($"/clients/{clientId}/bills/{secondBill}"))!;
        Assert.Equal(40m, secondView.OpenBalance); // 100 - 60 applied
    }

    /// <summary>§9.8 — Voiding the bill through the module's own void surface (<c>POST
    /// /bills/{id}/void</c>, which internally reverses the entry it owns using the module's own credential,
    /// not the caller's raw <c>gl.reverse</c>) drops the bill's Bill-axis fold-derived open to zero in the
    /// SAME read used before the void — there is no separate module mirror that could lag or diverge from
    /// the ledger.</summary>
    [Fact]
    public async Task Voiding_through_the_module_drops_the_bills_open_to_zero()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChart(controller, clientId);
        Guid vendor = await CreateVendorAsync(clerk, clientId);
        Guid billId = await EnterBillAsync(clerk, approver, clientId, vendor, 100m, fixture.RentExpenseAccountId);

        Assert.Equal(-100m, await BillFoldAsync(clerk, clientId, billId));
        BillView before = (await clerk.GetFromJsonAsync<BillView>($"/clients/{clientId}/bills/{billId}"))!;
        Assert.Equal(100m, before.OpenBalance);

        // Voiding an entered (posted) bill reverses its posted entry — the Controller holds both ap.write
        // (the module void) and gl.reverse.
        Bill voided = (await (await controller.PostAsJsonAsync(
                $"/clients/{clientId}/bills/{billId}/void", new VoidReasonRequest("duplicate")))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<Bill>())!;
        Assert.Equal(BillStatus.Void, voided.Status);

        EntryResponse reversal = Assert.Single((await clerk.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={billId}"))!, e => e.ReversalOf is not null);
        (await approver.PostAsync($"/clients/{clientId}/entries/{reversal.Id}/approve", null)).EnsureSuccessStatusCode();

        Assert.Equal(0m, await BillFoldAsync(clerk, clientId, billId));

        BillView after = (await clerk.GetFromJsonAsync<BillView>($"/clients/{clientId}/bills/{billId}"))!;
        Assert.Equal(0m, after.OpenBalance);
    }

    /// <summary>§9.9 — the sign proof. Vendor Credits is a debit-normal ASSET, so an unapplied overpayment
    /// reads as a POSITIVE credit balance — no negation. This is the mirror of AR's Customer Credits (a
    /// liability, which IS negated); a blind copy of the AR negation here would read the balance negative.
    /// Checked at both the derived credit-balance endpoint and the raw Vendor-axis fold on the Vendor
    /// Credits account.</summary>
    [Fact]
    public async Task Vendor_credit_balance_reads_positive_for_an_unapplied_overpayment()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChart(controller, clientId);
        Guid vendor = await CreateVendorAsync(clerk, clientId);
        Guid billId = await EnterBillAsync(clerk, approver, clientId, vendor, 40m, fixture.RentExpenseAccountId);

        // Pay 100, allocate only 40 to the bill -> 60 unapplied -> 60 vendor credit (an overpayment).
        BillPayment payment = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
                new RecordBillPaymentRequest(vendor, new DateOnly(2026, 3, 31), 100m, "check", [new Allocation(billId, 40m)])))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<BillPayment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, payment.Id);

        VendorCreditBalanceResponse creditBalance = (await clerk.GetFromJsonAsync<VendorCreditBalanceResponse>(
            $"/clients/{clientId}/vendors/{vendor}/credit-balance"))!;
        Assert.Equal(60m, creditBalance.CreditBalance);
        Assert.True(creditBalance.CreditBalance > 0m);

        decimal rawFold = (await clerk.GetFromJsonAsync<SubledgerResponse>(
                $"/clients/{clientId}/subledger?account={fixture.VendorCreditsAccountId}&dimension=Vendor"))!
            .Lines.Single(l => l.DimensionValue == vendor).Balance;
        Assert.Equal(60m, rawFold); // NOT negated — Vendor Credits is debit-normal.
        Assert.NotEqual(-60m, rawFold);
    }

    /// <summary>§9.10 — After a payment, the persisted payment document carries no allocation array. The
    /// compile-time half of this claim is that <see cref="BillPayment"/> has no <c>Allocations</c> member
    /// at all — there is nothing for a re-read to even deserialize into (asserted here via reflection, so
    /// this test fails loudly, not silently, if the property is ever reintroduced). This test proves the
    /// runtime half: a fresh GET of the payment round-trips its real fields, and the bill's fold-derived
    /// open — the only place the per-bill split now lives — still reflects the relief correctly. Same
    /// scenario as
    /// <see cref="FoldReadTests.Payment_persists_no_allocation_array_and_bill_fold_still_reads_open_balance"/>;
    /// restated here because making it explicit IS this suite's job for §9.10.</summary>
    [Fact]
    public async Task Payment_persists_no_allocation_array_and_the_bills_open_still_reads_correctly()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChart(controller, clientId);
        Guid vendor = await CreateVendorAsync(clerk, clientId);
        Guid billId = await EnterBillAsync(clerk, approver, clientId, vendor, 100m, fixture.RentExpenseAccountId);

        BillPayment payment = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
                new RecordBillPaymentRequest(vendor, new DateOnly(2026, 3, 31), 30m, "check", [new Allocation(billId, 30m)])))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<BillPayment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, payment.Id);

        Assert.Null(typeof(BillPayment).GetProperty("Allocations"));

        BillPayment[] byVendor = (await clerk.GetFromJsonAsync<BillPayment[]>(
            $"/clients/{clientId}/bill-payments?vendorId={vendor}"))!;
        BillPayment reread = Assert.Single(byVendor, p => p.Id == payment.Id);
        Assert.Equal(30m, reread.Amount);
        Assert.False(reread.Voided);
        // No Assert against an Allocations property is possible — BillPayment has none to assert against.

        BillView view = (await clerk.GetFromJsonAsync<BillView>($"/clients/{clientId}/bills/{billId}"))!;
        Assert.Equal(70m, view.OpenBalance);
    }

    // Private helper record to deserialize the anonymous credit-balance response (matches FoldReadTests).
    private sealed record VendorCreditBalanceResponse(Guid VendorId, decimal CreditBalance);
}
