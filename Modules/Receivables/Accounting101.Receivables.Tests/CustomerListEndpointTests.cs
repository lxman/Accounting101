using System.Net;
using System.Net.Http.Json;
using Accounting101.Receivables.Api;

namespace Accounting101.Receivables.Tests;

/// <summary>
/// Proves GET /clients/{clientId}/customers returns all customers for that client ordered by Name
/// ascending, and that a different clientId sees an empty array (client isolation).
/// </summary>
public sealed class CustomerListEndpointTests(ReceivablesHostFixture fixture) : IClassFixture<ReceivablesHostFixture>
{
    [Fact]
    public async Task List_returns_customers_ordered_by_name_ascending()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();

        // POST in reverse alphabetical order to prove ordering is applied, not just insertion order.
        (await http.PostAsJsonAsync($"/clients/{clientId}/customers", new CreateCustomerRequest("Beta LLC", null)))
            .EnsureSuccessStatusCode();
        (await http.PostAsJsonAsync($"/clients/{clientId}/customers", new CreateCustomerRequest("Acme Co", "acme@example.com")))
            .EnsureSuccessStatusCode();

        HttpResponseMessage response = await http.GetAsync($"/clients/{clientId}/customers");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Customer[] customers = (await response.Content.ReadFromJsonAsync<Customer[]>())!;
        Assert.Equal(2, customers.Length);
        Assert.Equal("Acme Co", customers[0].Name);
        Assert.Equal("Beta LLC", customers[1].Name);
        Assert.NotEqual(Guid.Empty, customers[0].Id);
        Assert.NotEqual(Guid.Empty, customers[1].Id);
    }

    [Fact]
    public async Task List_for_a_different_client_returns_empty_array()
    {
        // Client A gets one customer; Client B (freshly seeded, no customers) should see an empty array.
        (Guid clientAId, HttpClient httpA) = await fixture.SeedClientAsync();
        (Guid clientBId, HttpClient httpB) = await fixture.SeedClientAsync();

        (await httpA.PostAsJsonAsync($"/clients/{clientAId}/customers", new CreateCustomerRequest("Isolated Co", null)))
            .EnsureSuccessStatusCode();

        HttpResponseMessage response = await httpB.GetAsync($"/clients/{clientBId}/customers");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Customer[] customers = (await response.Content.ReadFromJsonAsync<Customer[]>())!;
        Assert.Empty(customers);
    }
}
