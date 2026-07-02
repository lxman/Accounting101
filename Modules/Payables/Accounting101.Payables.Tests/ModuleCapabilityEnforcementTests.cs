using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Api.Control;
using Accounting101.Payables.Api;

namespace Accounting101.Payables.Tests;

/// <summary>
/// Slice E: the payables HTTP surface enforces ap.write for vendor create and ap.read for the vendor
/// list. An auditor (every module .read, no module .write) and a wrong-module clerk (ArClerk: only
/// gl.read/ar.read/ar.write) are refused with 403 on create; the auditor's list read succeeds.
/// </summary>
public sealed class ModuleCapabilityEnforcementTests(PayablesHostFixture fixture) : IClassFixture<PayablesHostFixture>
{
    [Fact]
    public async Task Auditor_creating_a_vendor_over_http_gets_403()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync(LedgerRole.Auditor);

        HttpResponseMessage resp = await http.PostAsJsonAsync(
            $"/clients/{clientId}/vendors", new CreateVendorRequest("Acme", null));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Wrong_module_clerk_creating_a_vendor_over_http_gets_403()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync(LedgerRole.ArClerk);

        HttpResponseMessage resp = await http.PostAsJsonAsync(
            $"/clients/{clientId}/vendors", new CreateVendorRequest("Acme", null));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Auditor_listing_vendors_over_http_succeeds()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync(LedgerRole.Auditor);

        HttpResponseMessage resp = await http.GetAsync($"/clients/{clientId}/vendors");

        resp.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Controller_creating_a_vendor_over_http_succeeds()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync(LedgerRole.Controller);

        HttpResponseMessage resp = await http.PostAsJsonAsync(
            $"/clients/{clientId}/vendors", new CreateVendorRequest("Acme", null));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }
}
