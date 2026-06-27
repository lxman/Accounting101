using System.Net;

namespace Accounting101.Receivables.Tests;

/// <summary>The invoice list requires a customer. A missing/empty customerId must produce a clean 400
/// (like the settlement-filter validation), not a leaked framework binding error.</summary>
public sealed class InvoiceListValidationTests(ReceivablesHostFixture fixture) : IClassFixture<ReceivablesHostFixture>
{
    [Fact]
    public async Task Listing_invoices_without_a_customerId_returns_400()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();

        HttpResponseMessage response = await http.GetAsync($"/clients/{clientId}/invoices?settlement=open");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // A structured ProblemDetails, not a leaked framework binding error / dev exception page.
        Assert.Contains("problem+json", response.Content.Headers.ContentType?.MediaType ?? "");
    }

    [Fact]
    public async Task Listing_invoices_with_an_empty_customerId_returns_400()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();

        HttpResponseMessage response = await http.GetAsync(
            $"/clients/{clientId}/invoices?customerId={Guid.Empty}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
