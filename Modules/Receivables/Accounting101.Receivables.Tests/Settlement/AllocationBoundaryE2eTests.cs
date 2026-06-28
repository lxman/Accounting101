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
        (Guid clientId, _, HttpClient clerk, HttpClient approver) = await ArrangeAsync();
        Guid customer = await CreateCustomerAsync(clerk, clientId);
        Guid invoice = await IssueInvoiceAsync(clerk, approver, clientId, customer, 100m);
        (await approver.PostAsJsonAsync($"/clients/{clientId}/invoices/{invoice}/void",
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
}
