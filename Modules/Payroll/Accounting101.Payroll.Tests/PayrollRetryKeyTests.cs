using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;
using Accounting101.Payroll.Api;
using Microsoft.AspNetCore.Mvc;

namespace Accounting101.Payroll.Tests;

/// <summary>The retry-key contract on POST /payroll-runs and /tax-remittances: absent key behaves as
/// before, a replayed key returns the stored document without creating a second one, and a key reused for
/// different financial content is a 422 relayed from the ledger.</summary>
public sealed class PayrollRetryKeyTests(PayrollHostFixture fixture) : IClassFixture<PayrollHostFixture>
{
    private async Task SetUpPayrollChartAsync(HttpClient controller, Guid clientId)
    {
        await PutAccountAsync(controller, clientId, fixture.SalariesExpenseAccountId,     "6000", "Salaries Expense",      "Expense",   null);
        await PutAccountAsync(controller, clientId, fixture.PayrollTaxExpenseAccountId,   "6100", "Payroll Tax Expense",   "Expense",   null);
        await PutAccountAsync(controller, clientId, fixture.CashAccountId,                "1000", "Cash",                  "Asset",     null);
        await PutAccountAsync(controller, clientId, fixture.WithholdingsPayableAccountId, "2200", "Withholdings Payable",  "Liability", null);
        await PutAccountAsync(controller, clientId, fixture.PayrollTaxesPayableAccountId, "2300", "Payroll Taxes Payable", "Liability", null);
    }

    private static async Task PutAccountAsync(HttpClient http, Guid clientId, Guid accountId,
        string number, string name, string type, string? requiredDimension)
    {
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{accountId}",
            new AccountRequest { Number = number, Name = name, Type = type, RequiredDimension = requiredDimension }))
            .EnsureSuccessStatusCode();
    }

    // ── Payroll Runs ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Run_without_a_key_the_post_creates_as_before()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        await SetUpPayrollChartAsync(controller, clientId);

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/payroll-runs",
            new RecordPayrollRunRequest(28_000m, 2_142m, 2_142m, 0m, 5_040m,
                new DateOnly(2026, 6, 30), "June payroll"));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }

    [Fact]
    public async Task Run_a_replayed_key_returns_the_stored_document_and_creates_no_second_one()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        await SetUpPayrollChartAsync(controller, clientId);

        Guid key = Guid.NewGuid();
        RecordPayrollRunRequest req = new(28_000m, 2_142m, 2_142m, 0m, 5_040m,
            new DateOnly(2026, 6, 30), "June payroll", key);

        HttpResponseMessage first = await clerk.PostAsJsonAsync($"/clients/{clientId}/payroll-runs", req);
        HttpResponseMessage second = await clerk.PostAsJsonAsync($"/clients/{clientId}/payroll-runs", req);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);   // replay, not a create

        PayrollRun a = (await first.Content.ReadFromJsonAsync<PayrollRun>())!;
        PayrollRun b = (await second.Content.ReadFromJsonAsync<PayrollRun>())!;
        Assert.Equal(key, a.Id);
        Assert.Equal(a.Id, b.Id);

        PagedResponse<PayrollRunView> all = (await clerk.GetFromJsonAsync<PagedResponse<PayrollRunView>>(
            $"/clients/{clientId}/payroll-runs"))!;
        Assert.Single(all.Items);
    }

    [Fact]
    public async Task Run_the_same_key_with_different_content_is_a_422()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        await SetUpPayrollChartAsync(controller, clientId);

        Guid key = Guid.NewGuid();
        await clerk.PostAsJsonAsync($"/clients/{clientId}/payroll-runs",
            new RecordPayrollRunRequest(28_000m, 2_142m, 2_142m, 0m, 5_040m,
                new DateOnly(2026, 6, 30), "June payroll", key));

        HttpResponseMessage divergent = await clerk.PostAsJsonAsync($"/clients/{clientId}/payroll-runs",
            new RecordPayrollRunRequest(30_000m, 2_142m, 2_142m, 0m, 5_040m,   // divergence: different gross
                new DateOnly(2026, 6, 30), "June payroll", key));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, divergent.StatusCode);
        ProblemDetails? problem = await divergent.Content.ReadFromJsonAsync<ProblemDetails>();

        // Pinned contract: the Angular helper in core/api/problem-details.ts matches on this exact text.
        Assert.Equal("An entry with this id already exists with different content.", problem!.Detail);
    }

    // ── Tax Remittances ──────────────────────────────────────────────────────

    [Fact]
    public async Task Remittance_without_a_key_the_post_creates_as_before()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        await SetUpPayrollChartAsync(controller, clientId);

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/tax-remittances",
            new RecordTaxRemittanceRequest(5_040m, 4_284m, new DateOnly(2026, 7, 15), "Q2 remittance"));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }

    [Fact]
    public async Task Remittance_a_replayed_key_returns_the_stored_document_and_creates_no_second_one()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        await SetUpPayrollChartAsync(controller, clientId);

        Guid key = Guid.NewGuid();
        RecordTaxRemittanceRequest req = new(5_040m, 4_284m, new DateOnly(2026, 7, 15), "Q2 remittance", key);

        HttpResponseMessage first = await clerk.PostAsJsonAsync($"/clients/{clientId}/tax-remittances", req);
        HttpResponseMessage second = await clerk.PostAsJsonAsync($"/clients/{clientId}/tax-remittances", req);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);   // replay, not a create

        TaxRemittance a = (await first.Content.ReadFromJsonAsync<TaxRemittance>())!;
        TaxRemittance b = (await second.Content.ReadFromJsonAsync<TaxRemittance>())!;
        Assert.Equal(key, a.Id);
        Assert.Equal(a.Id, b.Id);

        PagedResponse<TaxRemittanceView> all = (await clerk.GetFromJsonAsync<PagedResponse<TaxRemittanceView>>(
            $"/clients/{clientId}/tax-remittances"))!;
        Assert.Single(all.Items);
    }

    [Fact]
    public async Task Remittance_the_same_key_with_different_content_is_a_422()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        await SetUpPayrollChartAsync(controller, clientId);

        Guid key = Guid.NewGuid();
        await clerk.PostAsJsonAsync($"/clients/{clientId}/tax-remittances",
            new RecordTaxRemittanceRequest(5_040m, 4_284m, new DateOnly(2026, 7, 15), "Q2 remittance", key));

        HttpResponseMessage divergent = await clerk.PostAsJsonAsync($"/clients/{clientId}/tax-remittances",
            new RecordTaxRemittanceRequest(6_000m, 4_284m, new DateOnly(2026, 7, 15), "Q2 remittance", key));  // divergence: different withholdings amount

        Assert.Equal(HttpStatusCode.UnprocessableEntity, divergent.StatusCode);
        ProblemDetails? problem = await divergent.Content.ReadFromJsonAsync<ProblemDetails>();

        // Pinned contract: the Angular helper in core/api/problem-details.ts matches on this exact text.
        Assert.Equal("An entry with this id already exists with different content.", problem!.Detail);
    }
}
