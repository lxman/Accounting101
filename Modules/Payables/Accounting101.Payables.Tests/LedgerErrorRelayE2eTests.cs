using System.Net;
using System.Net.Http.Json;
using Accounting101.Payables.Api;
using Accounting101.Settlement;
using Microsoft.AspNetCore.Mvc;
using static Accounting101.Payables.Tests.BillSettlementScenario;

namespace Accounting101.Payables.Tests;

/// <summary>A ledger refusal on a disposition path is relayed with the engine's real status (a 4xx),
/// not an opaque 500. Exercised via voiding a bill payment whose entry now falls in a closed period: the
/// module-owned-entry guard (2026-07-09) authorizes the module's own void call via its module credential
/// regardless of the caller's raw GL permissions, so the period freeze is the refusal that survives to
/// prove the relay — the engine still refuses (409), and the module must relay it, not swallow it into 500.</summary>
public sealed class LedgerErrorRelayE2eTests(PayablesHostFixture fixture) : IClassFixture<PayablesHostFixture>
{
    [Fact]
    public async Task Voiding_a_bill_payment_dated_into_a_closed_period_relays_the_ledger_status_not_500()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId, fixture);
        Guid vendor = await CreateVendorAsync(clerk, clientId);
        Guid bill = await EnterBillAsync(clerk, approver, clientId, vendor, 100m, fixture.RentExpenseAccountId);

        var paymentDate = new DateOnly(2026, 3, 31);
        BillPayment pay = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
                new RecordBillPaymentRequest(vendor, paymentDate, 100m, "check", [new Allocation(bill, 100m)])))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<BillPayment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, pay.Id);

        // Close the period the bill payment's entry is dated in. The clerk holds ap.write, so the module's
        // own void call (module-credentialed) is authorized regardless of raw GL permissions — but the
        // engine still refuses to mutate an entry whose effective date is now frozen.
        (await controller.PostAsJsonAsync($"/clients/{clientId}/periods/close", new { asOf = paymentDate }))
            .EnsureSuccessStatusCode();

        HttpResponseMessage resp = await clerk.PostAsJsonAsync(
            $"/clients/{clientId}/bill-payments/{pay.Id}/void", new VoidReasonRequest("oops"));

        Assert.NotEqual(HttpStatusCode.InternalServerError, resp.StatusCode);
        Assert.InRange((int)resp.StatusCode, 400, 499);
        ProblemDetails? problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.False(string.IsNullOrWhiteSpace(problem!.Detail));
    }
}
