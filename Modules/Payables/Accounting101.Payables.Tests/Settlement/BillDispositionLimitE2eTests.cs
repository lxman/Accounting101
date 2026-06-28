using System.Net;
using System.Net.Http.Json;
using Accounting101.Payables.Api;
using Accounting101.Settlement;
using static Accounting101.Payables.Tests.BillSettlementScenario;

namespace Accounting101.Payables.Tests;

/// <summary>Vendor-credit over-application (422) and bill-payment void guards (409).</summary>
public sealed class BillDispositionLimitE2eTests(PayablesHostFixture fixture) : IClassFixture<PayablesHostFixture>
{
    private async Task<(Guid clientId, HttpClient clerk, HttpClient approver)> ArrangeAsync()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId, fixture);
        return (clientId, clerk, approver);
    }

    [Fact]
    public async Task VendorCreditApplication_exceeding_available_credit_is_rejected()
    {
        (Guid clientId, HttpClient clerk, HttpClient approver) = await ArrangeAsync();
        Guid vendor = await CreateVendorAsync(clerk, clientId);
        Guid bill1 = await EnterBillAsync(clerk, approver, clientId, vendor, 100m, fixture.RentExpenseAccountId);

        // Overpay bill1 by 10 → vendor credit 10.
        BillPayment pay = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
                new RecordBillPaymentRequest(vendor, new DateOnly(2026, 3, 5), 110m, "check", [new Allocation(bill1, 100m)])))
            .Content.ReadFromJsonAsync<BillPayment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, pay.Id);

        Guid bill2 = await EnterBillAsync(clerk, approver, clientId, vendor, 100m, fixture.RentExpenseAccountId);
        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/vendor-credit-applications",
            new VendorCreditApplicationRequest(vendor, new DateOnly(2026, 3, 6), [new Allocation(bill2, 50m)]));

        await AssertProblemAsync(resp, HttpStatusCode.UnprocessableEntity, "exceeds available credit");
    }

    [Fact]
    public async Task Voiding_an_already_voided_bill_payment_is_rejected()
    {
        (Guid clientId, HttpClient clerk, HttpClient approver) = await ArrangeAsync();
        Guid vendor = await CreateVendorAsync(clerk, clientId);
        Guid bill = await EnterBillAsync(clerk, approver, clientId, vendor, 100m, fixture.RentExpenseAccountId);

        BillPayment pay = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
                new RecordBillPaymentRequest(vendor, new DateOnly(2026, 3, 5), 100m, "check", [new Allocation(bill, 100m)])))
            .Content.ReadFromJsonAsync<BillPayment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, pay.Id);

        // Voiding an approved (posted) bill payment reverses a posted GL entry — an Approver action. Both
        // voids go through the approver; the second hits the already-voided guard before any ledger call.
        (await approver.PostAsJsonAsync($"/clients/{clientId}/bill-payments/{pay.Id}/void",
            new VoidReasonRequest("first void"))).EnsureSuccessStatusCode();

        HttpResponseMessage resp = await approver.PostAsJsonAsync($"/clients/{clientId}/bill-payments/{pay.Id}/void",
            new VoidReasonRequest("second void"));

        await AssertProblemAsync(resp, HttpStatusCode.Conflict, "already voided");
    }

    [Fact]
    public async Task Voiding_a_nonexistent_bill_payment_is_rejected()
    {
        (Guid clientId, HttpClient clerk, _) = await ArrangeAsync();

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments/{Guid.NewGuid()}/void",
            new VoidReasonRequest("nope"));

        await AssertProblemAsync(resp, HttpStatusCode.Conflict, "not found");
    }
}
