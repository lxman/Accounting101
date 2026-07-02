using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Api.Control;
using Accounting101.Payroll.Api;

namespace Accounting101.Payroll.Tests;

/// <summary>
/// Slice E: the payroll HTTP surface enforces payroll.write for recording a run and payroll.read for
/// the run list. An auditor (every module .read, no module .write) and a wrong-module clerk (ArClerk:
/// only gl.read/ar.read/ar.write) are refused with 403 on create — the chokepoint fires on the first
/// document-store write, before any posting-account resolution, so no chart setup is required. The
/// auditor's list read succeeds.
/// </summary>
public sealed class ModuleCapabilityEnforcementTests(PayrollHostFixture fixture) : IClassFixture<PayrollHostFixture>
{
    // A known-good body (mirrors PayrollE2eTests) so the request reaches the capability chokepoint
    // instead of failing model binding / validation first.
    private static RecordPayrollRunRequest ValidRunRequest() => new(
        Gross: 28000m, EmployeeFica: 2142m, EmployerFica: 2142m, Deductions: 0m, IncomeTaxWithheld: 5040m,
        PayDate: new DateOnly(2026, 6, 30), Memo: "June payroll");

    [Fact]
    public async Task Auditor_recording_a_payroll_run_over_http_gets_403()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync(LedgerRole.Auditor);

        HttpResponseMessage resp = await http.PostAsJsonAsync($"/clients/{clientId}/payroll-runs", ValidRunRequest());

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Wrong_module_clerk_recording_a_payroll_run_over_http_gets_403()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync(LedgerRole.ArClerk);

        HttpResponseMessage resp = await http.PostAsJsonAsync($"/clients/{clientId}/payroll-runs", ValidRunRequest());

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Auditor_listing_payroll_runs_over_http_succeeds()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync(LedgerRole.Auditor);

        HttpResponseMessage resp = await http.GetAsync($"/clients/{clientId}/payroll-runs");

        resp.EnsureSuccessStatusCode();
    }
}
