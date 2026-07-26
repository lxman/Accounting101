using System.Net;
using System.Net.Http.Json;
using Accounting101.Payables.Api;
using Accounting101.Settlement;
using Microsoft.AspNetCore.Mvc;
using static Accounting101.Payables.Tests.BillSettlementScenario;

namespace Accounting101.Payables.Tests;

/// <summary>The retry-key contract on POST /bill-payments: absent key behaves as before, a replayed key
/// returns the stored payment without creating a second one, and a key reused for different financial
/// content is a 422 relayed from the ledger.</summary>
public sealed class BillPaymentRetryKeyTests(PayablesHostFixture fixture) : IClassFixture<PayablesHostFixture>
{
    [Fact]
    public async Task Without_a_key_the_post_creates_as_before()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId, fixture);
        Guid vendor = await CreateVendorAsync(clerk, clientId);
        Guid bill = await EnterBillAsync(clerk, approver, clientId, vendor, 100m, fixture.RentExpenseAccountId);

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
            new RecordBillPaymentRequest(vendor, new DateOnly(2026, 3, 1), 100m, "check", [new Allocation(bill, 100m)]));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }

    [Fact]
    public async Task A_replayed_key_returns_the_stored_payment_and_creates_no_second_one()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId, fixture);
        Guid vendor = await CreateVendorAsync(clerk, clientId);
        Guid bill = await EnterBillAsync(clerk, approver, clientId, vendor, 100m, fixture.RentExpenseAccountId);

        Guid key = Guid.NewGuid();
        RecordBillPaymentRequest req = new(vendor, new DateOnly(2026, 3, 1), 100m, "check", [new Allocation(bill, 100m)], key);

        HttpResponseMessage first = await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments", req);
        HttpResponseMessage second = await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments", req);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);   // replay, not a create

        BillPayment a = (await first.Content.ReadFromJsonAsync<BillPayment>())!;
        BillPayment b = (await second.Content.ReadFromJsonAsync<BillPayment>())!;
        Assert.Equal(key, a.Id);
        Assert.Equal(a.Id, b.Id);

        BillPayment[] all = (await clerk.GetFromJsonAsync<BillPayment[]>(
            $"/clients/{clientId}/bill-payments?vendorId={vendor}"))!;
        Assert.Single(all);
    }

    [Fact]
    public async Task The_same_key_with_different_content_is_a_422()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId, fixture);
        Guid vendor = await CreateVendorAsync(clerk, clientId);
        Guid bill = await EnterBillAsync(clerk, approver, clientId, vendor, 100m, fixture.RentExpenseAccountId);

        Guid key = Guid.NewGuid();
        await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
            new RecordBillPaymentRequest(vendor, new DateOnly(2026, 3, 1), 100m, "check", [new Allocation(bill, 100m)], key));

        HttpResponseMessage divergent = await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
            new RecordBillPaymentRequest(vendor, new DateOnly(2026, 3, 1), 60m, "check", [new Allocation(bill, 60m)], key));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, divergent.StatusCode);
        ProblemDetails? problem = await divergent.Content.ReadFromJsonAsync<ProblemDetails>();

        // Pinned contract: the Angular helper in core/api/problem-details.ts matches on this exact text.
        Assert.Equal("An entry with this id already exists with different content.", problem!.Detail);
    }
}
