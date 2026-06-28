using System.Net;
using System.Net.Http.Json;
using Accounting101.Payables.Api;
using Accounting101.Settlement;
using static Accounting101.Payables.Tests.BillSettlementScenario;

namespace Accounting101.Payables.Tests;

/// <summary>Allocation-boundary rejections on POST /bill-payments — each maps to 422 with a specific reason.</summary>
public sealed class BillAllocationBoundaryE2eTests(PayablesHostFixture fixture) : IClassFixture<PayablesHostFixture>
{
    private async Task<(Guid clientId, HttpClient clerk, HttpClient approver)> ArrangeAsync()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId, fixture);
        return (clientId, clerk, approver);
    }

    [Fact]
    public async Task Allocations_exceeding_payment_amount_are_rejected()
    {
        (Guid clientId, HttpClient clerk, HttpClient approver) = await ArrangeAsync();
        Guid vendor = await CreateVendorAsync(clerk, clientId);
        Guid bill = await EnterBillAsync(clerk, approver, clientId, vendor, 100m, fixture.RentExpenseAccountId);

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
            new RecordBillPaymentRequest(vendor, new DateOnly(2026, 3, 5), 50m, "check", [new Allocation(bill, 80m)]));

        await AssertProblemAsync(resp, HttpStatusCode.UnprocessableEntity, "cannot exceed the payment amount");
    }

    [Fact]
    public async Task Allocation_exceeding_open_balance_is_rejected()
    {
        (Guid clientId, HttpClient clerk, HttpClient approver) = await ArrangeAsync();
        Guid vendor = await CreateVendorAsync(clerk, clientId);
        Guid bill = await EnterBillAsync(clerk, approver, clientId, vendor, 100m, fixture.RentExpenseAccountId);

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
            new RecordBillPaymentRequest(vendor, new DateOnly(2026, 3, 5), 200m, "check", [new Allocation(bill, 200m)]));

        await AssertProblemAsync(resp, HttpStatusCode.UnprocessableEntity, "exceeds its open balance");
    }

    [Fact]
    public async Task Allocation_to_a_voided_bill_is_rejected()
    {
        (Guid clientId, HttpClient clerk, HttpClient approver) = await ArrangeAsync();
        Guid vendor = await CreateVendorAsync(clerk, clientId);
        Guid bill = await EnterBillAsync(clerk, approver, clientId, vendor, 100m, fixture.RentExpenseAccountId);
        // Voiding an entered bill reverses its posted entry — an Approver action (matches the AR voided-invoice case).
        (await approver.PostAsJsonAsync($"/clients/{clientId}/bills/{bill}/void",
            new VoidReasonRequest("test"))).EnsureSuccessStatusCode();

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
            new RecordBillPaymentRequest(vendor, new DateOnly(2026, 3, 6), 50m, "check", [new Allocation(bill, 50m)]));

        await AssertProblemAsync(resp, HttpStatusCode.UnprocessableEntity, "only entered bills can be paid");
    }

    [Fact]
    public async Task Paying_an_already_settled_bill_is_rejected()
    {
        (Guid clientId, HttpClient clerk, HttpClient approver) = await ArrangeAsync();
        Guid vendor = await CreateVendorAsync(clerk, clientId);
        Guid bill = await EnterBillAsync(clerk, approver, clientId, vendor, 100m, fixture.RentExpenseAccountId);

        BillPayment paid = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
                new RecordBillPaymentRequest(vendor, new DateOnly(2026, 3, 5), 100m, "check", [new Allocation(bill, 100m)])))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<BillPayment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, paid.Id);

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
            new RecordBillPaymentRequest(vendor, new DateOnly(2026, 3, 6), 50m, "check", [new Allocation(bill, 50m)]));

        await AssertProblemAsync(resp, HttpStatusCode.UnprocessableEntity, "exceeds its open balance");
    }

    [Fact]
    public async Task Allocation_to_a_nonexistent_bill_is_rejected()
    {
        (Guid clientId, HttpClient clerk, _) = await ArrangeAsync();
        Guid vendor = await CreateVendorAsync(clerk, clientId);

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
            new RecordBillPaymentRequest(vendor, new DateOnly(2026, 3, 5), 50m, "check", [new Allocation(Guid.NewGuid(), 50m)]));

        await AssertProblemAsync(resp, HttpStatusCode.UnprocessableEntity, "does not exist");
    }

    [Fact]
    public async Task Allocation_to_a_draft_bill_is_rejected()
    {
        (Guid clientId, HttpClient clerk, _) = await ArrangeAsync();
        Guid vendor = await CreateVendorAsync(clerk, clientId);
        Guid draft = await DraftBillAsync(clerk, clientId, vendor, 100m, fixture.RentExpenseAccountId);

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
            new RecordBillPaymentRequest(vendor, new DateOnly(2026, 3, 5), 50m, "check", [new Allocation(draft, 50m)]));

        await AssertProblemAsync(resp, HttpStatusCode.UnprocessableEntity, "only entered bills can be paid");
    }

    [Fact]
    public async Task Allocation_to_another_vendors_bill_is_rejected()
    {
        (Guid clientId, HttpClient clerk, HttpClient approver) = await ArrangeAsync();
        Guid vendorA = await CreateVendorAsync(clerk, clientId, "PropCo");
        Guid vendorB = await CreateVendorAsync(clerk, clientId, "UtilCo");
        Guid billA = await EnterBillAsync(clerk, approver, clientId, vendorA, 100m, fixture.RentExpenseAccountId);

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
            new RecordBillPaymentRequest(vendorB, new DateOnly(2026, 3, 5), 50m, "check", [new Allocation(billA, 50m)]));

        await AssertProblemAsync(resp, HttpStatusCode.UnprocessableEntity, "belongs to a different vendor");
    }

    [Fact]
    public async Task Zero_payment_amount_is_rejected()
    {
        (Guid clientId, HttpClient clerk, _) = await ArrangeAsync();
        Guid vendor = await CreateVendorAsync(clerk, clientId);

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
            new RecordBillPaymentRequest(vendor, new DateOnly(2026, 3, 5), 0m, "check", []));

        await AssertProblemAsync(resp, HttpStatusCode.UnprocessableEntity, "payment amount must be greater than zero");
    }

    [Fact]
    public async Task Negative_allocation_amount_is_rejected()
    {
        (Guid clientId, HttpClient clerk, HttpClient approver) = await ArrangeAsync();
        Guid vendor = await CreateVendorAsync(clerk, clientId);
        Guid bill = await EnterBillAsync(clerk, approver, clientId, vendor, 100m, fixture.RentExpenseAccountId);

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
            new RecordBillPaymentRequest(vendor, new DateOnly(2026, 3, 5), 50m, "check", [new Allocation(bill, -10m)]));

        await AssertProblemAsync(resp, HttpStatusCode.UnprocessableEntity, "allocation amount must be greater than zero");
    }
}
