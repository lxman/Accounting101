using System.Net;
using System.Net.Http.Json;
using Accounting101.Payables.Api;
using Accounting101.Settlement;
using Microsoft.AspNetCore.Mvc;
using static Accounting101.Payables.Tests.BillSettlementScenario;

namespace Accounting101.Payables.Tests;

/// <summary>The retry-key contract on POST /vendor-credit-applications: absent key behaves as before, a
/// replayed key returns the stored application without creating a second one, and a key reused for
/// different financial content is a 422 relayed from the ledger.</summary>
public sealed class VendorCreditRetryKeyTests(PayablesHostFixture fixture) : IClassFixture<PayablesHostFixture>
{
    /// <summary>Records a bill payment whose amount exceeds its allocation, leaving the remainder as vendor
    /// credit (BillPosting.ComposeBillPayment routes it to the vendor-credits account) — the only way to
    /// have credit available to apply. Approves the payment's own entry: the credit balance is folded
    /// Posted-only, so an unapproved overpayment would read as zero available credit.</summary>
    private static async Task GiveVendorCreditAsync(
        HttpClient clerk, HttpClient approver, Guid clientId, Guid vendorId, Guid overpaidBill, decimal overpaidBillAmount, decimal overpayBy)
    {
        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
            new RecordBillPaymentRequest(vendorId, new DateOnly(2026, 3, 1), overpaidBillAmount + overpayBy, "check",
                [new Allocation(overpaidBill, overpaidBillAmount)]));
        resp.EnsureSuccessStatusCode();
        BillPayment payment = (await resp.Content.ReadFromJsonAsync<BillPayment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, payment.Id);
    }

    [Fact]
    public async Task Without_a_key_the_post_creates_as_before()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId, fixture);
        Guid vendor = await CreateVendorAsync(clerk, clientId);
        Guid overpaidBill = await EnterBillAsync(clerk, approver, clientId, vendor, 100m, fixture.RentExpenseAccountId);
        await GiveVendorCreditAsync(clerk, approver, clientId, vendor, overpaidBill, 100m, 50m);
        Guid targetBill = await EnterBillAsync(clerk, approver, clientId, vendor, 200m, fixture.RentExpenseAccountId);

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/vendor-credit-applications",
            new VendorCreditApplicationRequest(vendor, new DateOnly(2026, 3, 2), [new Allocation(targetBill, 50m)]));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }

    [Fact]
    public async Task A_replayed_key_returns_the_stored_application_and_creates_no_second_one()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId, fixture);
        Guid vendor = await CreateVendorAsync(clerk, clientId);
        Guid overpaidBill = await EnterBillAsync(clerk, approver, clientId, vendor, 100m, fixture.RentExpenseAccountId);
        await GiveVendorCreditAsync(clerk, approver, clientId, vendor, overpaidBill, 100m, 50m);
        Guid targetBill = await EnterBillAsync(clerk, approver, clientId, vendor, 200m, fixture.RentExpenseAccountId);

        Guid key = Guid.NewGuid();
        VendorCreditApplicationRequest req = new(vendor, new DateOnly(2026, 3, 2), [new Allocation(targetBill, 50m)], key);

        HttpResponseMessage first = await clerk.PostAsJsonAsync($"/clients/{clientId}/vendor-credit-applications", req);
        HttpResponseMessage second = await clerk.PostAsJsonAsync($"/clients/{clientId}/vendor-credit-applications", req);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);   // replay, not a create

        VendorCreditApplication a = (await first.Content.ReadFromJsonAsync<VendorCreditApplication>())!;
        VendorCreditApplication b = (await second.Content.ReadFromJsonAsync<VendorCreditApplication>())!;
        Assert.Equal(key, a.Id);
        Assert.Equal(a.Id, b.Id);

        VendorCreditApplicationWithAllocations[] all = (await clerk.GetFromJsonAsync<VendorCreditApplicationWithAllocations[]>(
            $"/clients/{clientId}/vendor-credit-applications?vendorId={vendor}"))!;
        Assert.Single(all);
    }

    [Fact]
    public async Task The_same_key_with_different_content_is_a_422()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId, fixture);
        Guid vendor = await CreateVendorAsync(clerk, clientId);
        Guid overpaidBill = await EnterBillAsync(clerk, approver, clientId, vendor, 100m, fixture.RentExpenseAccountId);
        await GiveVendorCreditAsync(clerk, approver, clientId, vendor, overpaidBill, 100m, 50m);
        Guid targetBill = await EnterBillAsync(clerk, approver, clientId, vendor, 200m, fixture.RentExpenseAccountId);

        Guid key = Guid.NewGuid();
        await clerk.PostAsJsonAsync($"/clients/{clientId}/vendor-credit-applications",
            new VendorCreditApplicationRequest(vendor, new DateOnly(2026, 3, 2), [new Allocation(targetBill, 50m)], key));

        HttpResponseMessage divergent = await clerk.PostAsJsonAsync($"/clients/{clientId}/vendor-credit-applications",
            new VendorCreditApplicationRequest(vendor, new DateOnly(2026, 3, 2), [new Allocation(targetBill, 30m)], key));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, divergent.StatusCode);
        ProblemDetails? problem = await divergent.Content.ReadFromJsonAsync<ProblemDetails>();

        // Pinned contract: the Angular helper in core/api/problem-details.ts matches on this exact text.
        Assert.Equal("An entry with this id already exists with different content.", problem!.Detail);
    }
}
