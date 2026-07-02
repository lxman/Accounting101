using System.Net;
using System.Net.Http.Json;
using Accounting101.Banking.Reconciliation.Api;
using Accounting101.Ledger.Api.Control;

namespace Accounting101.Banking.Reconciliation.Tests;

/// <summary>
/// Slice E: the reconciliation HTTP surface enforces bankrec.write for recording a bank statement and
/// bankrec.read for the statement list. An auditor (every module .read, no module .write) and a
/// wrong-module clerk (ArClerk: only gl.read/ar.read/ar.write) are refused with 403 on create.
/// Recording a statement is a pure document create (no GL post, no chart setup needed), so the
/// Controller positive control is included too. The auditor's list read succeeds.
/// </summary>
public sealed class ModuleCapabilityEnforcementTests(ReconciliationHostFixture fixture) : IClassFixture<ReconciliationHostFixture>
{
    private RecordBankStatementRequest ValidStatementRequest()
    {
        DateOnly date = new(2026, 1, 20);
        DateOnly stmtDate = new(2026, 1, 31);
        return new RecordBankStatementRequest(fixture.CashAccountId, stmtDate, 0m, 60m,
            [
                new BankStatementLineRequest(date, 100m, "deposit", null),
                new BankStatementLineRequest(date, -40m, "payment", null),
            ]);
    }

    [Fact]
    public async Task Auditor_recording_a_bank_statement_over_http_gets_403()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync(LedgerRole.Auditor);

        HttpResponseMessage resp = await http.PostAsJsonAsync($"/clients/{clientId}/bank-statements", ValidStatementRequest());

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Wrong_module_clerk_recording_a_bank_statement_over_http_gets_403()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync(LedgerRole.ArClerk);

        HttpResponseMessage resp = await http.PostAsJsonAsync($"/clients/{clientId}/bank-statements", ValidStatementRequest());

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Auditor_listing_bank_statements_over_http_succeeds()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync(LedgerRole.Auditor);

        HttpResponseMessage resp = await http.GetAsync($"/clients/{clientId}/bank-statements?cashAccountId={fixture.CashAccountId}");

        resp.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Controller_recording_a_bank_statement_over_http_succeeds()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync(LedgerRole.Controller);

        HttpResponseMessage resp = await http.PostAsJsonAsync($"/clients/{clientId}/bank-statements", ValidStatementRequest());

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }
}
