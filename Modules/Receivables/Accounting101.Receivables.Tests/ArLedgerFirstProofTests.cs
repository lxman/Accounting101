using System.Net;
using System.Net.Http.Json;
using Accounting101.Receivables.Api;
using Accounting101.Ledger.Contracts;
using Accounting101.Settlement;
using static Accounting101.Receivables.Tests.SettlementScenario;

namespace Accounting101.Receivables.Tests;

/// <summary>
/// Task 9 — the merge-gate proof suite for the AR ledger-first slice. Codifies the spec's §9 proof
/// obligations as ONE explicit set of end-to-end tests: every fact a caller can observe about an
/// invoice's or a customer's open balance is folded from the ledger (dimension-tagged A/R lines), never
/// read from a module-owned mirror. Every assertion here reads the ledger fold or a
/// <c>/subledger[/reconciliation]</c> response — never a module-stored amount (<c>Payment</c>,
/// <c>WriteOff</c>, <c>CreditNote</c>, and <c>CreditApplication</c> no longer even carry an allocation
/// array to read).
/// <para>
/// Several obligations are also exercised — at the recipe / entry-line level rather than the
/// obligation level — by tests written during Tasks 3-8. Where that is true this suite still asserts the
/// obligation directly (reads the fold / reconciliation, not entry lines), so it is not a pure
/// duplicate; the one true exception is §9.6, which is not restated here at all because
/// <see cref="ArRequiresInvoiceTests.Raw_AR_line_without_Invoice_dimension_is_rejected"/> already proves
/// it exactly as an engine-level guarantee, independent of any module recipe.
/// </para>
/// </summary>
public sealed class ArLedgerFirstProofTests(ReceivablesHostFixture fixture) : IClassFixture<ReceivablesHostFixture>
{
    private Task SetUpChartAsync(HttpClient controller, Guid clientId) =>
        SettlementScenario.SetUpChartAsync(controller, clientId, fixture);

    private async Task<decimal> InvoiceFoldAsync(HttpClient http, Guid clientId, Guid invoiceId)
    {
        SubledgerResponse fold = (await http.GetFromJsonAsync<SubledgerResponse>(
            $"/clients/{clientId}/subledger?account={fixture.ReceivableAccountId}&dimension=Invoice"))!;
        SubledgerLineResponse? line = fold.Lines.SingleOrDefault(l => l.DimensionValue == invoiceId);
        return line?.Balance ?? 0m; // an invoice with no on-the-books line at all folds to zero
    }

    /// <summary>§9.1 — Issue → the invoice's Invoice-axis fold equals its full total. Taxable, so the
    /// total (108) differs from the pre-tax line amount (100): the fold must match the invoice's real
    /// total, not just its subtotal.</summary>
    [Fact]
    public async Task Issue_sets_the_invoices_fold_to_the_full_total()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);
        Guid customer = await CreateCustomerAsync(http, clientId);

        Guid invoiceId = await IssueInvoiceAsync(http, http, clientId, customer, 100m, taxRate: 0.08m, taxable: true);

        InvoiceView view = (await http.GetFromJsonAsync<InvoiceView>($"/clients/{clientId}/invoices/{invoiceId}"))!;
        Assert.Equal(108m, view.Invoice.Total);

        Assert.Equal(108m, await InvoiceFoldAsync(http, clientId, invoiceId));
    }

    /// <summary>§9.2 — An approved partial payment (one allocation) reduces the invoice's fold by exactly
    /// the allocation.</summary>
    [Fact]
    public async Task Approved_partial_payment_reduces_the_invoices_fold_by_the_allocation()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);
        Guid customer = await CreateCustomerAsync(http, clientId);
        Guid invoiceId = await IssueInvoiceAsync(http, http, clientId, customer, 100m);

        Payment payment = (await (await http.PostAsJsonAsync($"/clients/{clientId}/payments",
                new RecordPaymentRequest(customer, new DateOnly(2026, 3, 5), 30m, "check", [new Allocation(invoiceId, 30m)])))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<Payment>())!;
        await ApproveBySourceRefAsync(http, http, clientId, payment.Id);

        Assert.Equal(70m, await InvoiceFoldAsync(http, clientId, invoiceId));
    }

    /// <summary>§9.3 — A split payment across two invoices reduces EACH invoice's fold by its own
    /// allocation, not a shared/aggregate amount. The recipe shape (one Invoice-tagged A/R line per
    /// allocation) is proven at the entry-line level by
    /// <see cref="PaymentDimensionTests.Split_payment_emits_one_Invoice_tagged_AR_line_per_allocation"/>;
    /// this test asserts the obligation itself, directly at the fold.</summary>
    [Fact]
    public async Task Split_payment_reduces_each_invoices_fold_by_its_own_allocation()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);
        Guid customer = await CreateCustomerAsync(http, clientId);
        Guid invoiceA = await IssueInvoiceAsync(http, http, clientId, customer, 100m);
        Guid invoiceB = await IssueInvoiceAsync(http, http, clientId, customer, 100m);

        Payment payment = (await (await http.PostAsJsonAsync($"/clients/{clientId}/payments",
                new RecordPaymentRequest(customer, new DateOnly(2026, 3, 31), 150m, "check",
                    [new Allocation(invoiceA, 100m), new Allocation(invoiceB, 50m)])))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<Payment>())!;
        await ApproveBySourceRefAsync(http, http, clientId, payment.Id);

        Assert.Equal(0m, await InvoiceFoldAsync(http, clientId, invoiceA));
        Assert.Equal(50m, await InvoiceFoldAsync(http, clientId, invoiceB));
    }

    /// <summary>§9.4 — The customer-axis fold equals the sum of that customer's open invoices, and both
    /// the Customer-axis and Invoice-axis reconciliations tie out (now that A/R requires BOTH dimensions,
    /// the Invoice-axis reconciliation endpoint no longer 422s — the Task 6 flip that enabled it).</summary>
    [Fact]
    public async Task Customer_axis_fold_equals_open_invoices_and_both_dimensions_tie_out()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);
        Guid customer = await CreateCustomerAsync(http, clientId);
        Guid invoiceA = await IssueInvoiceAsync(http, http, clientId, customer, 100m);
        Guid invoiceB = await IssueInvoiceAsync(http, http, clientId, customer, 100m);

        Payment payment = (await (await http.PostAsJsonAsync($"/clients/{clientId}/payments",
                new RecordPaymentRequest(customer, new DateOnly(2026, 3, 5), 40m, "check", [new Allocation(invoiceA, 40m)])))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<Payment>())!;
        await ApproveBySourceRefAsync(http, http, clientId, payment.Id);

        CustomerAccountView account = (await http.GetFromJsonAsync<CustomerAccountView>(
            $"/clients/{clientId}/customers/{customer}/account?asOf=2026-04-01"))!;
        decimal sumOfOpenInvoices = account.OpenInvoices.Sum(l => l.OpenBalance);
        Assert.Equal(160m, sumOfOpenInvoices); // (100 - 40) + 100

        SubledgerResponse customerFold = (await http.GetFromJsonAsync<SubledgerResponse>(
            $"/clients/{clientId}/subledger?account={fixture.ReceivableAccountId}&dimension=Customer"))!;
        decimal customerBalance = customerFold.Lines.Single(l => l.DimensionValue == customer).Balance;
        Assert.Equal(sumOfOpenInvoices, customerBalance);

        SubledgerReconciliationResponse byCustomer = (await http.GetFromJsonAsync<SubledgerReconciliationResponse>(
            $"/clients/{clientId}/subledger/reconciliation?account={fixture.ReceivableAccountId}&dimension=Customer"))!;
        Assert.True(byCustomer.TiesOut);

        SubledgerReconciliationResponse byInvoice = (await http.GetFromJsonAsync<SubledgerReconciliationResponse>(
            $"/clients/{clientId}/subledger/reconciliation?account={fixture.ReceivableAccountId}&dimension=Invoice"))!;
        Assert.True(byInvoice.TiesOut);
    }

    /// <summary>§9.5 — Over-application is impossible from either angle: (a) a single allocation
    /// exceeding the invoice's open balance (read from the fold) is rejected, and the fold is unchanged by
    /// the rejected attempt; (b) two unapplied (unapproved) payments each for the invoice's full open
    /// balance — the second is rejected AT RECORD, before either is approved, because validation reserves
    /// against pending-plus-posted reliefs, not just what is already on the books. The rejection messages
    /// are also pinned by <c>AllocationBoundaryE2eTests.Allocation_exceeding_open_balance_is_rejected</c>
    /// and <c>.Second_unapproved_payment_over_applying_the_same_invoice_is_rejected</c>; this test adds the
    /// fold-level assertions those don't make.</summary>
    [Fact]
    public async Task Overapplication_is_rejected_against_open_balance_and_against_pending_reliefs()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);
        Guid customer = await CreateCustomerAsync(http, clientId);
        Guid invoiceId = await IssueInvoiceAsync(http, http, clientId, customer, 100m);

        // (a) A single allocation over the invoice's whole open balance (100) is rejected outright.
        HttpResponseMessage overResp = await http.PostAsJsonAsync($"/clients/{clientId}/payments",
            new RecordPaymentRequest(customer, new DateOnly(2026, 3, 5), 150m, "check", [new Allocation(invoiceId, 150m)]));
        await AssertProblemAsync(overResp, HttpStatusCode.UnprocessableEntity, "exceeds its open balance");
        Assert.Equal(100m, await InvoiceFoldAsync(http, clientId, invoiceId)); // untouched by the rejected attempt

        // (b) Payment A: alloc 100, recorded but deliberately left unapproved — nothing hits the books yet,
        // so the Posted-only read fold still shows the invoice fully open.
        HttpResponseMessage payAResp = await http.PostAsJsonAsync($"/clients/{clientId}/payments",
            new RecordPaymentRequest(customer, new DateOnly(2026, 3, 6), 100m, "check", [new Allocation(invoiceId, 100m)]));
        payAResp.EnsureSuccessStatusCode();
        Assert.Equal(100m, await InvoiceFoldAsync(http, clientId, invoiceId));

        // Payment B, also 100 against the same invoice while A is still pending, is rejected at record.
        HttpResponseMessage payBResp = await http.PostAsJsonAsync($"/clients/{clientId}/payments",
            new RecordPaymentRequest(customer, new DateOnly(2026, 3, 7), 100m, "check", [new Allocation(invoiceId, 100m)]));
        await AssertProblemAsync(payBResp, HttpStatusCode.UnprocessableEntity, "exceeds its open balance");
    }

    // §9.6 — a raw A/R line missing the Invoice dimension is rejected 422, naming the missing dimension.
    // Already proven, exactly, engine-level, independent of any module recipe, by
    // ArRequiresInvoiceTests.Raw_AR_line_without_Invoice_dimension_is_rejected. Not duplicated here.

    /// <summary>§9.7 — An approved write-off (a disposition, not a payment) relieves an invoice via the
    /// same dimensioned-line recipe; the invoice's fold reduces by exactly the write-off amount. Checked
    /// both via the raw fold and via the module's own <c>InvoiceView</c> read surface.</summary>
    [Fact]
    public async Task Approved_writeoff_reduces_the_invoices_fold_by_the_writeoff_amount()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);
        Guid customer = await CreateCustomerAsync(http, clientId);
        Guid invoiceId = await IssueInvoiceAsync(http, http, clientId, customer, 100m);

        WriteOff writeOff = (await (await http.PostAsJsonAsync($"/clients/{clientId}/write-offs",
                new WriteOffRequest(customer, new DateOnly(2026, 3, 6), [new Allocation(invoiceId, 40m)], "uncollectible")))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<WriteOff>())!;
        await ApproveBySourceRefAsync(http, http, clientId, writeOff.Id);

        Assert.Equal(60m, await InvoiceFoldAsync(http, clientId, invoiceId));

        InvoiceView view = (await http.GetFromJsonAsync<InvoiceView>($"/clients/{clientId}/invoices/{invoiceId}"))!;
        Assert.Equal(60m, view.OpenBalance);
    }

    /// <summary>§9.8 — Voiding the invoice through the module's own void surface (<c>POST
    /// /invoices/{id}/void</c>, which internally reverses the entry it owns using the module's own
    /// credential, not the caller's raw <c>gl.reverse</c>) drops the invoice's Invoice-axis fold to zero
    /// in the SAME read used before the void — there is no separate module mirror that could lag or
    /// diverge from the ledger.</summary>
    [Fact]
    public async Task Voiding_through_the_module_drops_the_invoices_fold_to_zero()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);
        Guid customer = await CreateCustomerAsync(clerk, clientId);
        Guid invoiceId = await IssueInvoiceAsync(clerk, approver, clientId, customer, 100m);

        Assert.Equal(100m, await InvoiceFoldAsync(clerk, clientId, invoiceId));

        Invoice voided = (await (await controller.PostAsJsonAsync(
                $"/clients/{clientId}/invoices/{invoiceId}/void", new VoidInvoiceRequest("duplicate")))
            .Content.ReadFromJsonAsync<Invoice>())!;
        Assert.Equal(InvoiceStatus.Void, voided.Status);

        EntryResponse reversal = Assert.Single((await clerk.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={invoiceId}"))!, e => e.ReversalOf is not null);
        (await approver.PostAsync($"/clients/{clientId}/entries/{reversal.Id}/approve", null)).EnsureSuccessStatusCode();

        Assert.Equal(0m, await InvoiceFoldAsync(clerk, clientId, invoiceId));
    }

    /// <summary>§9.9 — After a payment, the persisted payment document carries no allocation array. The
    /// compile-time half of this claim is that <c>Payment</c> (see
    /// Modules/Receivables/Accounting101.Receivables/Payment.cs) has no <c>Allocations</c> member at all —
    /// there is nothing for a re-read to even deserialize into. This test proves the runtime half: a fresh
    /// GET of the payment round-trips its real fields, and the invoice's fold — the only place the
    /// per-invoice split now lives — still reflects the relief correctly. Same scenario as
    /// <see cref="FoldReadTests.Payment_persists_no_allocation_array_yet_folds_correctly"/>; restated here
    /// because making it explicit IS this suite's job for §9.9.</summary>
    [Fact]
    public async Task Payment_persists_no_allocation_array_and_the_fold_still_reads_correctly()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);
        Guid customer = await CreateCustomerAsync(http, clientId);
        Guid invoiceId = await IssueInvoiceAsync(http, http, clientId, customer, 100m);

        Payment payment = (await (await http.PostAsJsonAsync($"/clients/{clientId}/payments",
                new RecordPaymentRequest(customer, new DateOnly(2026, 3, 31), 30m, "check", [new Allocation(invoiceId, 30m)])))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<Payment>())!;
        await ApproveBySourceRefAsync(http, http, clientId, payment.Id);

        Payment reread = Assert.Single((await http.GetFromJsonAsync<Payment[]>(
            $"/clients/{clientId}/payments?customerId={customer}"))!);
        Assert.Equal(payment.Id, reread.Id);
        Assert.Equal(30m, reread.Amount);
        Assert.False(reread.Voided);
        // No Assert against an Allocations property is possible — Payment has none to assert against.

        Assert.Equal(70m, await InvoiceFoldAsync(http, clientId, invoiceId));
    }
}
