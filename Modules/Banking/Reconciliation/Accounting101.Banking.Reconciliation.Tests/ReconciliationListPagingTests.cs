using System.Net;
using System.Net.Http.Json;
using Accounting101.Banking.Reconciliation;
using Accounting101.Banking.Reconciliation.Api;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Banking.Reconciliation.Tests;

/// <summary>
/// Focused paging tests for the ListStatements and ListAdjustments endpoints (computed in-memory
/// paging after the service materializes the full list). Proves: skip/limit pages correctly, Total
/// is the full count regardless of page, and an invalid 'order' value returns a clean 400.
/// The service calls and their underlying store reads remain unbounded — paging is endpoint-only.
/// </summary>
public sealed class ReconciliationListPagingTests(ReconciliationHostFixture fixture) : IClassFixture<ReconciliationHostFixture>
{
    private static Task PutAccountAsync(HttpClient http, Guid clientId, Guid id, string number, string name, string type) =>
        http.PutAsJsonAsync($"/clients/{clientId}/accounts/{id}", new AccountRequest { Number = number, Name = name, Type = type })
            .ContinueWith(t => t.Result.EnsureSuccessStatusCode());

    // ── Statements ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ListStatements_invalid_order_returns_400()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, _) = await fixture.SeedSodClientAsync();

        HttpResponseMessage resp = await clerk.GetAsync(
            $"/clients/{clientId}/bank-statements?cashAccountId={fixture.CashAccountId}&order=invalid");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("problem+json", resp.Content.Headers.ContentType?.MediaType ?? "");
    }

    [Fact]
    public async Task ListStatements_page_1_returns_limit_items_and_correct_total()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, _) = await fixture.SeedSodClientAsync();

        // Record 3 statements for the same cash account (no ledger posting required).
        for (int i = 1; i <= 3; i++)
            await RecordStatementAsync(clerk, clientId, fixture.CashAccountId, i);

        PagedResponse<BankStatement> page = (await clerk.GetFromJsonAsync<PagedResponse<BankStatement>>(
            $"/clients/{clientId}/bank-statements?cashAccountId={fixture.CashAccountId}&limit=2"))!;

        Assert.Equal(3, page.Total);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal(0, page.Skip);
        Assert.Equal(2, page.Limit);
    }

    [Fact]
    public async Task ListStatements_page_2_returns_remaining_item()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, _) = await fixture.SeedSodClientAsync();

        for (int i = 1; i <= 3; i++)
            await RecordStatementAsync(clerk, clientId, fixture.CashAccountId, i);

        PagedResponse<BankStatement> page1 = (await clerk.GetFromJsonAsync<PagedResponse<BankStatement>>(
            $"/clients/{clientId}/bank-statements?cashAccountId={fixture.CashAccountId}&limit=2&skip=0"))!;
        PagedResponse<BankStatement> page2 = (await clerk.GetFromJsonAsync<PagedResponse<BankStatement>>(
            $"/clients/{clientId}/bank-statements?cashAccountId={fixture.CashAccountId}&limit=2&skip=2"))!;

        Assert.Equal(3, page1.Total);
        Assert.Equal(2, page1.Items.Count);
        Assert.Equal(3, page2.Total);
        Assert.Single(page2.Items);
        Assert.DoesNotContain(page2.Items[0].Id, page1.Items.Select(s => s.Id));
    }

    // ── Adjustments ────────────────────────────────────────────────────────

    [Fact]
    public async Task ListAdjustments_invalid_order_returns_400()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        await PutAccountAsync(controller, clientId, fixture.CashAccountId, "1000", "Cash", "Asset");

        BankStatement stmt = await RecordStatementAsync(clerk, clientId, fixture.CashAccountId, 1);
        Reconciliation rec = (await (await clerk.PostAsJsonAsync(
            $"/clients/{clientId}/reconciliations", new StartReconciliationRequest(stmt.Id)))
            .Content.ReadFromJsonAsync<Reconciliation>())!;

        HttpResponseMessage resp = await clerk.GetAsync(
            $"/clients/{clientId}/reconciliations/{rec.Id}/adjustments?order=invalid");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("problem+json", resp.Content.Headers.ContentType?.MediaType ?? "");
    }

    [Fact]
    public async Task ListAdjustments_page_1_returns_limit_items_and_correct_total()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await PutAccountAsync(controller, clientId, fixture.CashAccountId, "1000", "Cash", "Asset");
        await PutAccountAsync(controller, clientId, fixture.InterestExpenseAccountId, "5000", "Interest Expense", "Expense");

        BankStatement stmt = await RecordStatementAsync(clerk, clientId, fixture.CashAccountId, 1);
        Reconciliation rec = (await (await clerk.PostAsJsonAsync(
            $"/clients/{clientId}/reconciliations", new StartReconciliationRequest(stmt.Id)))
            .Content.ReadFromJsonAsync<Reconciliation>())!;

        // Record 3 adjustments (> limit=2 so pagination splits them).
        for (int i = 0; i < 3; i++)
            await RecordAdjustmentAsync(clerk, clientId, rec.Id, fixture.InterestExpenseAccountId);

        PagedResponse<BankAdjustment> page = (await clerk.GetFromJsonAsync<PagedResponse<BankAdjustment>>(
            $"/clients/{clientId}/reconciliations/{rec.Id}/adjustments?limit=2"))!;

        Assert.Equal(3, page.Total);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal(0, page.Skip);
        Assert.Equal(2, page.Limit);
    }

    [Fact]
    public async Task ListAdjustments_page_2_returns_remaining_item()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await PutAccountAsync(controller, clientId, fixture.CashAccountId, "1000", "Cash", "Asset");
        await PutAccountAsync(controller, clientId, fixture.InterestExpenseAccountId, "5000", "Interest Expense", "Expense");

        BankStatement stmt = await RecordStatementAsync(clerk, clientId, fixture.CashAccountId, 1);
        Reconciliation rec = (await (await clerk.PostAsJsonAsync(
            $"/clients/{clientId}/reconciliations", new StartReconciliationRequest(stmt.Id)))
            .Content.ReadFromJsonAsync<Reconciliation>())!;

        for (int i = 0; i < 3; i++)
            await RecordAdjustmentAsync(clerk, clientId, rec.Id, fixture.InterestExpenseAccountId);

        PagedResponse<BankAdjustment> page1 = (await clerk.GetFromJsonAsync<PagedResponse<BankAdjustment>>(
            $"/clients/{clientId}/reconciliations/{rec.Id}/adjustments?limit=2&skip=0"))!;
        PagedResponse<BankAdjustment> page2 = (await clerk.GetFromJsonAsync<PagedResponse<BankAdjustment>>(
            $"/clients/{clientId}/reconciliations/{rec.Id}/adjustments?limit=2&skip=2"))!;

        Assert.Equal(3, page1.Total);
        Assert.Equal(2, page1.Items.Count);
        Assert.Equal(3, page2.Total);
        Assert.Single(page2.Items);
        Assert.DoesNotContain(page2.Items[0].Id, page1.Items.Select(a => a.Id));
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static async Task<BankStatement> RecordStatementAsync(
        HttpClient clerk, Guid clientId, Guid cashAccountId, int dayOffset)
    {
        DateOnly date = new DateOnly(2026, 1, 1).AddDays(dayOffset - 1);
        return (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bank-statements",
                new RecordBankStatementRequest(cashAccountId, date, 0m, 100m,
                    [new BankStatementLineRequest(date, 100m, "deposit", null)])))
            .Content.ReadFromJsonAsync<BankStatement>())!;
    }

    private static async Task RecordAdjustmentAsync(
        HttpClient clerk, Guid clientId, Guid reconciliationId, Guid offsetAccountId)
    {
        (await clerk.PostAsJsonAsync($"/clients/{clientId}/reconciliations/{reconciliationId}/adjustments",
            new RecordAdjustmentRequest(offsetAccountId, 5m, AdjustmentKind.Charge, null, "fee")))
            .EnsureSuccessStatusCode();
    }
}
