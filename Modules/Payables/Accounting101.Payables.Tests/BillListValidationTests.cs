using System.Net;

namespace Accounting101.Payables.Tests;

/// <summary>The bill list requires a vendor. A missing/empty vendorId must produce a clean 400 (like the
/// settlement-filter validation), not a leaked framework binding error.</summary>
public sealed class BillListValidationTests(PayablesHostFixture fixture) : IClassFixture<PayablesHostFixture>
{
    [Fact]
    public async Task Listing_bills_without_a_vendorId_returns_400()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();

        HttpResponseMessage response = await http.GetAsync($"/clients/{clientId}/bills?settlement=open");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // A structured ProblemDetails, not a leaked framework binding error / dev exception page.
        Assert.Contains("problem+json", response.Content.Headers.ContentType?.MediaType ?? "");
    }

    [Fact]
    public async Task Listing_bills_with_an_empty_vendorId_returns_400()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();

        HttpResponseMessage response = await http.GetAsync(
            $"/clients/{clientId}/bills?vendorId={Guid.Empty}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
