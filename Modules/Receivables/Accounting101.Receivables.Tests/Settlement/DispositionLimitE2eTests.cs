using System.Net;
using System.Net.Http.Json;
using Accounting101.Receivables.Api;
using Accounting101.Settlement;
using static Accounting101.Receivables.Tests.SettlementScenario;

namespace Accounting101.Receivables.Tests;

/// <summary>Disposition-limit rejections: over-disposition (422) and void guards (409).</summary>
public sealed class DispositionLimitE2eTests(ReceivablesHostFixture fixture) : IClassFixture<ReceivablesHostFixture>
{
    private async Task<(Guid clientId, HttpClient clerk, HttpClient approver)> ArrangeAsync()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId, fixture);
        return (clientId, clerk, approver);
    }

    [Fact]
    public async Task WriteOff_over_open_balance_is_rejected()
    {
        (Guid clientId, HttpClient clerk, HttpClient approver) = await ArrangeAsync();
        Guid customer = await CreateCustomerAsync(clerk, clientId);
        Guid invoice = await IssueInvoiceAsync(clerk, approver, clientId, customer, 100m);

        Payment pay = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
                new RecordPaymentRequest(customer, new DateOnly(2026, 3, 5), 60m, "check", [new Allocation(invoice, 60m)])))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<Payment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, pay.Id);

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/write-offs",
            new WriteOffRequest(customer, new DateOnly(2026, 3, 6), [new Allocation(invoice, 50m)], "uncollectible"));

        await AssertProblemAsync(resp, HttpStatusCode.UnprocessableEntity, "exceeds its open balance");
    }

    [Fact]
    public async Task CreditNote_over_open_balance_is_rejected()
    {
        (Guid clientId, HttpClient clerk, HttpClient approver) = await ArrangeAsync();
        Guid customer = await CreateCustomerAsync(clerk, clientId);
        Guid invoice = await IssueInvoiceAsync(clerk, approver, clientId, customer, 100m);

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/credit-notes",
            new CreditNoteRequest(customer, new DateOnly(2026, 3, 6), [new Allocation(invoice, 150m)], "too much"));

        await AssertProblemAsync(resp, HttpStatusCode.UnprocessableEntity, "exceeds its open balance");
    }

    [Fact]
    public async Task Refund_exceeding_available_credit_is_rejected()
    {
        (Guid clientId, HttpClient clerk, HttpClient approver) = await ArrangeAsync();
        Guid customer = await CreateCustomerAsync(clerk, clientId);
        Guid invoice = await IssueInvoiceAsync(clerk, approver, clientId, customer, 100m);

        // Overpay by 20 → customer credit 20.
        Payment pay = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
                new RecordPaymentRequest(customer, new DateOnly(2026, 3, 5), 120m, "check", [new Allocation(invoice, 100m)])))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<Payment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, pay.Id);

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/refunds",
            new RefundRequest(customer, new DateOnly(2026, 3, 6), 50m, "too much"));

        await AssertProblemAsync(resp, HttpStatusCode.UnprocessableEntity, "exceeds available credit");
    }

    [Fact]
    public async Task CreditApplication_exceeding_available_credit_is_rejected()
    {
        (Guid clientId, HttpClient clerk, HttpClient approver) = await ArrangeAsync();
        Guid customer = await CreateCustomerAsync(clerk, clientId);
        Guid invoice1 = await IssueInvoiceAsync(clerk, approver, clientId, customer, 100m);

        // Overpay by 10 → customer credit 10.
        Payment pay = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
                new RecordPaymentRequest(customer, new DateOnly(2026, 3, 5), 110m, "check", [new Allocation(invoice1, 100m)])))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<Payment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, pay.Id);

        Guid invoice2 = await IssueInvoiceAsync(clerk, approver, clientId, customer, 100m);
        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/credit-applications",
            new CreditApplicationRequest(customer, new DateOnly(2026, 3, 6), [new Allocation(invoice2, 50m)]));

        await AssertProblemAsync(resp, HttpStatusCode.UnprocessableEntity, "exceeds available credit");
    }

    [Fact]
    public async Task Voiding_an_already_voided_payment_is_rejected()
    {
        (Guid clientId, HttpClient clerk, HttpClient approver) = await ArrangeAsync();
        Guid customer = await CreateCustomerAsync(clerk, clientId);
        Guid invoice = await IssueInvoiceAsync(clerk, approver, clientId, customer, 100m);

        Payment pay = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
                new RecordPaymentRequest(customer, new DateOnly(2026, 3, 5), 100m, "check", [new Allocation(invoice, 100m)])))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<Payment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, pay.Id);

        (await approver.PostAsJsonAsync($"/clients/{clientId}/payments/{pay.Id}/void",
            new VoidInvoiceRequest("first void"))).EnsureSuccessStatusCode();

        HttpResponseMessage resp = await approver.PostAsJsonAsync($"/clients/{clientId}/payments/{pay.Id}/void",
            new VoidInvoiceRequest("second void"));

        await AssertProblemAsync(resp, HttpStatusCode.Conflict, "already voided");
    }

    [Fact]
    public async Task Voiding_a_nonexistent_payment_is_rejected()
    {
        (Guid clientId, HttpClient clerk, _) = await ArrangeAsync();

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/payments/{Guid.NewGuid()}/void",
            new VoidInvoiceRequest("nope"));

        await AssertProblemAsync(resp, HttpStatusCode.Conflict, "not found");
    }
}
