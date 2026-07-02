using System.Net;
using System.Net.Http.Json;
using Accounting101.Banking.Cash.Api;
using Accounting101.Ledger.Api.Control;

namespace Accounting101.Banking.Cash.Tests;

/// <summary>
/// Slice E: the cash HTTP surface enforces cash.write for recording a disbursement and cash.read for
/// the disbursement list. An auditor (every module .read, no module .write) and a wrong-module clerk
/// (ArClerk: only gl.read/ar.read/ar.write) are refused with 403 on create — the chokepoint fires on
/// the first document-store write, before any posting-account resolution, so no chart setup is
/// required. The auditor's list read succeeds.
/// </summary>
public sealed class ModuleCapabilityEnforcementTests(CashHostFixture fixture) : IClassFixture<CashHostFixture>
{
    // A known-good body (mirrors CashE2eTests) so the request reaches the capability chokepoint
    // instead of failing model binding / validation first.
    private RecordCashDisbursementRequest ValidDisbursementRequest() => new(
        Lines: [
            new CashLineRequest(fixture.InterestExpenseAccountId, 500m),
            new CashLineRequest(fixture.LoanPayableAccountId, 1500m),
        ],
        Date: new DateOnly(2026, 6, 30),
        Reference: "LOAN-2026-06",
        Memo: "Loan payment — interest + principal");

    [Fact]
    public async Task Auditor_recording_a_disbursement_over_http_gets_403()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync(LedgerRole.Auditor);

        HttpResponseMessage resp = await http.PostAsJsonAsync($"/clients/{clientId}/cash-disbursements", ValidDisbursementRequest());

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Wrong_module_clerk_recording_a_disbursement_over_http_gets_403()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync(LedgerRole.ArClerk);

        HttpResponseMessage resp = await http.PostAsJsonAsync($"/clients/{clientId}/cash-disbursements", ValidDisbursementRequest());

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Auditor_listing_disbursements_over_http_succeeds()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync(LedgerRole.Auditor);

        HttpResponseMessage resp = await http.GetAsync($"/clients/{clientId}/cash-disbursements");

        resp.EnsureSuccessStatusCode();
    }
}
