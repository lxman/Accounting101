using System.Net;
using System.Net.Http.Json;

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
}
