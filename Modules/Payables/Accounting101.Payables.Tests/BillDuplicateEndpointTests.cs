using System.Net;
using System.Net.Http.Json;
using Accounting101.Payables.Api;

namespace Accounting101.Payables.Tests;

/// <summary>The duplicate lookup over HTTP. Both query parameters are required, and the result is
/// scoped to one vendor — another vendor's bill with the same reference is not this vendor's duplicate.</summary>
public sealed class BillDuplicateEndpointTests(PayablesHostFixture fixture) : IClassFixture<PayablesHostFixture>
{
    [Fact]
    public async Task Without_a_vendorId_returns_400()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();

        HttpResponseMessage response = await http.GetAsync($"/clients/{clientId}/bills/duplicates?reference=INV-9");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("problem+json", response.Content.Headers.ContentType?.MediaType ?? "");
    }

    [Fact]
    public async Task Without_a_reference_returns_400()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();

        HttpResponseMessage response = await http.GetAsync(
            $"/clients/{clientId}/bills/duplicates?vendorId={Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("problem+json", response.Content.Headers.ContentType?.MediaType ?? "");
    }

    [Fact]
    public async Task With_no_matching_bills_returns_an_empty_array()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();

        HttpResponseMessage response = await http.GetAsync(
            $"/clients/{clientId}/bills/duplicates?vendorId={Guid.NewGuid()}&reference=INV-9");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        List<Bill>? matches = await response.Content.ReadFromJsonAsync<List<Bill>>();
        Assert.NotNull(matches);
        Assert.Empty(matches);
    }

    /// <summary>The draft-visibility assertion the endpoint exists for: nothing else in this file seeds
    /// a bill, so a wrong argument order or a mis-bound query key would still leave every test here
    /// green while every import silently reported "no duplicates found".</summary>
    [Fact]
    public async Task With_a_matching_draft_bill_returns_it()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        await BillSettlementScenario.SetUpChartAsync(controller, clientId, fixture);
        Guid vendorId = await BillSettlementScenario.CreateVendorAsync(clerk, clientId);

        DraftBillRequest req = new(vendorId, new DateOnly(2026, 3, 1), null, "REF", null,
            [new BillLineBody("Rent", 1000m, fixture.RentExpenseAccountId)]);
        Bill draft = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bills", req))
            .Content.ReadFromJsonAsync<Bill>())!;

        HttpResponseMessage response = await clerk.GetAsync(
            $"/clients/{clientId}/bills/duplicates?vendorId={vendorId}&reference=REF");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        List<Bill>? matches = await response.Content.ReadFromJsonAsync<List<Bill>>();
        Assert.NotNull(matches);
        Bill match = Assert.Single(matches);
        Assert.Equal(draft.Id, match.Id);
        Assert.Equal(BillStatus.Draft, match.Status);
    }
}
