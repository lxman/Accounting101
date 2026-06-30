using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;
using Accounting101.Payables.Api;
using Accounting101.Settlement;

namespace Accounting101.Payables.Tests;

/// <summary>Proves GET /clients/{clientId}/bill-payments?vendorId returns the vendor's recorded
/// payments, 400s without vendorId, and is client-isolated.</summary>
public sealed class BillPaymentListEndpointTests(PayablesHostFixture fixture) : IClassFixture<PayablesHostFixture>
{
    private static async Task PutAccountAsync(HttpClient http, Guid clientId, Guid accountId,
        string number, string name, string type, string? requiredDimension) =>
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{accountId}",
            new AccountRequest { Number = number, Name = name, Type = type, RequiredDimension = requiredDimension }))
            .EnsureSuccessStatusCode();

    [Fact]
    public async Task Lists_a_vendors_recorded_payments()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) =
            await fixture.SeedSodClientAsync();
        await PutAccountAsync(controller, clientId, fixture.CashAccountId,          "1000", "Cash",           "Asset", null);
        await PutAccountAsync(controller, clientId, fixture.VendorCreditsAccountId, "1300", "Vendor Credits", "Asset", "Vendor");
        await PutAccountAsync(controller, clientId, fixture.PayableAccountId,       "2000", "Accounts Payable","Liability", "Vendor");

        Vendor vendor = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/vendors",
            new CreateVendorRequest("PropCo", null))).Content.ReadFromJsonAsync<Vendor>())!;

        // A pure prepayment (no allocations) → full amount becomes vendor credit; no bill needed.
        RecordBillPaymentRequest req = new(vendor.Id, new DateOnly(2026, 3, 1), 500m, "check", []);
        (await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments", req)).EnsureSuccessStatusCode();

        BillPayment[] payments = (await clerk.GetFromJsonAsync<BillPayment[]>(
            $"/clients/{clientId}/bill-payments?vendorId={vendor.Id}"))!;
        Assert.Single(payments);
        Assert.Equal(500m, payments[0].Amount);
        Assert.Equal(vendor.Id, payments[0].VendorId);
    }

    [Fact]
    public async Task Requires_vendorId()
    {
        (Guid clientId, _, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        HttpResponseMessage response = await clerk.GetAsync($"/clients/{clientId}/bill-payments");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Is_client_isolated()
    {
        (Guid clientAId, _, HttpClient clerkA, _) = await fixture.SeedSodClientAsync();
        (Guid clientBId, _, HttpClient clerkB, _) = await fixture.SeedSodClientAsync();
        Vendor vendorB = (await (await clerkB.PostAsJsonAsync($"/clients/{clientBId}/vendors",
            new CreateVendorRequest("Other", null))).Content.ReadFromJsonAsync<Vendor>())!;

        // Client A asks for client B's vendor id → empty (A has no such payments).
        BillPayment[] payments = (await clerkA.GetFromJsonAsync<BillPayment[]>(
            $"/clients/{clientAId}/bill-payments?vendorId={vendorB.Id}"))!;
        Assert.Empty(payments);
    }
}
