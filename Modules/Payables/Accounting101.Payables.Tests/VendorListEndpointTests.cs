using System.Net;
using System.Net.Http.Json;
using Accounting101.Payables.Api;

namespace Accounting101.Payables.Tests;

/// <summary>Proves GET /clients/{clientId}/vendors returns the client's vendors ordered by Name,
/// and that a different client sees an empty array (isolation).</summary>
public sealed class VendorListEndpointTests(PayablesHostFixture fixture) : IClassFixture<PayablesHostFixture>
{
    [Fact]
    public async Task List_returns_vendors_ordered_by_name_ascending()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();

        (await http.PostAsJsonAsync($"/clients/{clientId}/vendors", new CreateVendorRequest("Zeta Supplies", null)))
            .EnsureSuccessStatusCode();
        (await http.PostAsJsonAsync($"/clients/{clientId}/vendors", new CreateVendorRequest("Acme Parts", "a@x.com")))
            .EnsureSuccessStatusCode();

        HttpResponseMessage response = await http.GetAsync($"/clients/{clientId}/vendors");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Vendor[] vendors = (await response.Content.ReadFromJsonAsync<Vendor[]>())!;
        Assert.Equal(2, vendors.Length);
        Assert.Equal("Acme Parts", vendors[0].Name);
        Assert.Equal("Zeta Supplies", vendors[1].Name);
    }

    [Fact]
    public async Task List_for_a_different_client_returns_empty_array()
    {
        (Guid clientAId, HttpClient httpA) = await fixture.SeedClientAsync();
        (Guid clientBId, HttpClient httpB) = await fixture.SeedClientAsync();

        (await httpA.PostAsJsonAsync($"/clients/{clientAId}/vendors", new CreateVendorRequest("Isolated Co", null)))
            .EnsureSuccessStatusCode();

        HttpResponseMessage response = await httpB.GetAsync($"/clients/{clientBId}/vendors");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Vendor[] vendors = (await response.Content.ReadFromJsonAsync<Vendor[]>())!;
        Assert.Empty(vendors);
    }
}
