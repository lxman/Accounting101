using System.Net;
using System.Net.Http.Json;
using Accounting101.Receivables.Api;
using Accounting101.Settlement;
using static Accounting101.Receivables.Tests.SettlementScenario;

namespace Accounting101.Receivables.Tests;

/// <summary>Allocation-boundary rejections on POST /payments — each maps to 422 with a specific reason.</summary>
public sealed class AllocationBoundaryE2eTests(ReceivablesHostFixture fixture) : IClassFixture<ReceivablesHostFixture>
{
    private async Task<(Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver)> ArrangeAsync()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId, fixture);
        return (clientId, controller, clerk, approver);
    }

    [Fact]
    public async Task Allocations_exceeding_payment_amount_are_rejected()
    {
        (Guid clientId, _, HttpClient clerk, HttpClient approver) = await ArrangeAsync();
        Guid customer = await CreateCustomerAsync(clerk, clientId);
        Guid invoice = await IssueInvoiceAsync(clerk, approver, clientId, customer, 100m);

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
            new RecordPaymentRequest(customer, new DateOnly(2026, 3, 5), 50m, "check", [new Allocation(invoice, 80m)]));

        await AssertProblemAsync(resp, HttpStatusCode.UnprocessableEntity, "cannot exceed the payment amount");
    }

    [Fact]
    public async Task Allocation_exceeding_open_balance_is_rejected()
    {
        (Guid clientId, _, HttpClient clerk, HttpClient approver) = await ArrangeAsync();
        Guid customer = await CreateCustomerAsync(clerk, clientId);
        Guid invoice = await IssueInvoiceAsync(clerk, approver, clientId, customer, 100m);

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
            new RecordPaymentRequest(customer, new DateOnly(2026, 3, 5), 200m, "check", [new Allocation(invoice, 200m)]));

        await AssertProblemAsync(resp, HttpStatusCode.UnprocessableEntity, "exceeds its open balance");
    }

    [Fact]
    public async Task Paying_an_already_settled_invoice_is_rejected()
    {
        (Guid clientId, _, HttpClient clerk, HttpClient approver) = await ArrangeAsync();
        Guid customer = await CreateCustomerAsync(clerk, clientId);
        Guid invoice = await IssueInvoiceAsync(clerk, approver, clientId, customer, 100m);

        Payment paid = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
                new RecordPaymentRequest(customer, new DateOnly(2026, 3, 5), 100m, "check", [new Allocation(invoice, 100m)])))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<Payment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, paid.Id);

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
            new RecordPaymentRequest(customer, new DateOnly(2026, 3, 6), 50m, "check", [new Allocation(invoice, 50m)]));

        await AssertProblemAsync(resp, HttpStatusCode.UnprocessableEntity, "exceeds its open balance");
    }

    [Fact]
    public async Task Allocation_to_a_nonexistent_invoice_is_rejected()
    {
        (Guid clientId, _, HttpClient clerk, _) = await ArrangeAsync();
        Guid customer = await CreateCustomerAsync(clerk, clientId);

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
            new RecordPaymentRequest(customer, new DateOnly(2026, 3, 5), 50m, "check", [new Allocation(Guid.NewGuid(), 50m)]));

        await AssertProblemAsync(resp, HttpStatusCode.UnprocessableEntity, "does not exist");
    }

    [Fact]
    public async Task Allocation_to_a_draft_invoice_is_rejected()
    {
        (Guid clientId, _, HttpClient clerk, _) = await ArrangeAsync();
        Guid customer = await CreateCustomerAsync(clerk, clientId);
        Guid draft = await DraftInvoiceAsync(clerk, clientId, customer, 100m);

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
            new RecordPaymentRequest(customer, new DateOnly(2026, 3, 5), 50m, "check", [new Allocation(draft, 50m)]));

        await AssertProblemAsync(resp, HttpStatusCode.UnprocessableEntity, "only issued invoices can be paid");
    }

    [Fact]
    public async Task Allocation_to_a_voided_invoice_is_rejected()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await ArrangeAsync();
        Guid customer = await CreateCustomerAsync(clerk, clientId);
        Guid invoice = await IssueInvoiceAsync(clerk, approver, clientId, customer, 100m);
        (await controller.PostAsJsonAsync($"/clients/{clientId}/invoices/{invoice}/void",
            new VoidInvoiceRequest("test"))).EnsureSuccessStatusCode();

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
            new RecordPaymentRequest(customer, new DateOnly(2026, 3, 6), 50m, "check", [new Allocation(invoice, 50m)]));

        await AssertProblemAsync(resp, HttpStatusCode.UnprocessableEntity, "only issued invoices can be paid");
    }

    [Fact]
    public async Task Allocation_to_another_customers_invoice_is_rejected()
    {
        (Guid clientId, _, HttpClient clerk, HttpClient approver) = await ArrangeAsync();
        Guid customerA = await CreateCustomerAsync(clerk, clientId, "Stark");
        Guid customerB = await CreateCustomerAsync(clerk, clientId, "Wayne");
        Guid invoiceA = await IssueInvoiceAsync(clerk, approver, clientId, customerA, 100m);

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
            new RecordPaymentRequest(customerB, new DateOnly(2026, 3, 5), 50m, "check", [new Allocation(invoiceA, 50m)]));

        await AssertProblemAsync(resp, HttpStatusCode.UnprocessableEntity, "belongs to a different customer");
    }

    [Fact]
    public async Task Zero_payment_amount_is_rejected()
    {
        (Guid clientId, _, HttpClient clerk, _) = await ArrangeAsync();
        Guid customer = await CreateCustomerAsync(clerk, clientId);

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
            new RecordPaymentRequest(customer, new DateOnly(2026, 3, 5), 0m, "check", []));

        await AssertProblemAsync(resp, HttpStatusCode.UnprocessableEntity, "payment amount must be greater than zero");
    }

    [Fact]
    public async Task Negative_allocation_amount_is_rejected()
    {
        (Guid clientId, _, HttpClient clerk, HttpClient approver) = await ArrangeAsync();
        Guid customer = await CreateCustomerAsync(clerk, clientId);
        Guid invoice = await IssueInvoiceAsync(clerk, approver, clientId, customer, 100m);

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
            new RecordPaymentRequest(customer, new DateOnly(2026, 3, 5), 50m, "check", [new Allocation(invoice, -10m)]));

        await AssertProblemAsync(resp, HttpStatusCode.UnprocessableEntity, "allocation amount must be greater than zero");
    }

    /// <summary>
    /// CRITICAL over-application hole (Task 7 finding 1): allocation validation used to source
    /// <c>alreadyApplied</c> from the Posted-only ledger fold, so two reliefs recorded against the same
    /// invoice while BOTH are still unapproved each validated against the same stale open balance (100).
    /// Payment A (100) posts PendingApproval — nothing is on the books yet — so Payment B (100) against
    /// the same invoice also saw open=100 and would have been accepted too, over-relieving the invoice to
    /// -100 once both were approved. The fix makes allocation validation reserve against PENDING-plus-
    /// posted (non-void) reliefs, so Payment B must be rejected here, before either payment is approved.
    /// </summary>
    [Fact]
    public async Task Second_unapproved_payment_over_applying_the_same_invoice_is_rejected()
    {
        (Guid clientId, _, HttpClient clerk, HttpClient approver) = await ArrangeAsync();
        Guid customer = await CreateCustomerAsync(clerk, clientId);
        Guid invoice = await IssueInvoiceAsync(clerk, approver, clientId, customer, 100m);

        // Payment A: alloc 100, recorded but deliberately left PendingApproval (nothing approves it).
        HttpResponseMessage payAResp = await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
            new RecordPaymentRequest(customer, new DateOnly(2026, 3, 5), 100m, "check", [new Allocation(invoice, 100m)]));
        payAResp.EnsureSuccessStatusCode();

        // Payment B: also alloc 100 against the same invoice, while A is still pending. Must be rejected —
        // A already reserves the invoice's whole open balance, even though nothing is on the books yet.
        HttpResponseMessage payBResp = await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
            new RecordPaymentRequest(customer, new DateOnly(2026, 3, 6), 100m, "check", [new Allocation(invoice, 100m)]));

        await AssertProblemAsync(payBResp, HttpStatusCode.UnprocessableEntity, "exceeds its open balance");
    }
}
