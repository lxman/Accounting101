using System.Net;
using System.Net.Http.Json;
using Accounting101.Banking.Cash;
using Accounting101.Banking.Cash.Api;
using Accounting101.Banking.Reconciliation;
using Accounting101.Banking.Reconciliation.Api;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Banking.Reconciliation.Tests;

/// <summary>End-to-end through the real host: post real cash entries via the Cash module, record a matching
/// bank statement, then reconcile — clearing the entries balances the worksheet and lets complete succeed;
/// a bank-only residual leaves a non-zero difference and blocks complete.</summary>
public sealed class ReconciliationE2eTests(ReconciliationHostFixture fixture) : IClassFixture<ReconciliationHostFixture>
{
    private static async Task SetUpChartAsync(HttpClient controller, Guid clientId, ReconciliationHostFixture f)
    {
        await PutAccountAsync(controller, clientId, f.CashAccountId, "1000", "Cash", "Asset");
        await PutAccountAsync(controller, clientId, f.MembersCapitalAccountId, "3000", "Members Capital", "Equity");
        await PutAccountAsync(controller, clientId, f.InterestExpenseAccountId, "5000", "Interest Expense", "Expense");
    }

    private static Task PutAccountAsync(HttpClient http, Guid clientId, Guid id, string number, string name, string type) =>
        http.PutAsJsonAsync($"/clients/{clientId}/accounts/{id}", new AccountRequest { Number = number, Name = name, Type = type })
            .ContinueWith(t => t.Result.EnsureSuccessStatusCode());

    private static async Task ApproveBySourceRefAsync(HttpClient reader, HttpClient approver, Guid clientId, Guid sourceRef)
    {
        EntryResponse[] entries = (await reader.GetFromJsonAsync<EntryResponse[]>($"/clients/{clientId}/entries?sourceRef={sourceRef}"))!;
        foreach (EntryResponse e in entries.Where(e => e.Posting == "PendingApproval"))
            (await approver.PostAsync($"/clients/{clientId}/entries/{e.Id}/approve", null)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Clearing_the_posted_cash_entries_balances_the_reconciliation_and_completes()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId, fixture);
        DateOnly date = new(2026, 1, 20);
        DateOnly stmtDate = new(2026, 1, 31);

        // A real deposit (Dr Cash 100) and disbursement (Cr Cash 40), both approved → posted.
        CashDeposit dep = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/cash-deposits",
                new RecordCashDepositRequest([new CashLineRequest(fixture.MembersCapitalAccountId, 100m)], date, "DEP", null)))
            .Content.ReadFromJsonAsync<CashDeposit>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, dep.Id);
        CashDisbursement dis = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/cash-disbursements",
                new RecordCashDisbursementRequest([new CashLineRequest(fixture.InterestExpenseAccountId, 40m)], date, "DIS", null)))
            .Content.ReadFromJsonAsync<CashDisbursement>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, dis.Id);

        // Statement: opening 0, +100 deposit, −40 payment, closing 60.
        BankStatement statement = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bank-statements",
                new RecordBankStatementRequest(fixture.CashAccountId, stmtDate, 0m, 60m,
                    [new BankStatementLineRequest(date, 100m, "deposit", null), new BankStatementLineRequest(date, -40m, "payment", null)])))
            .Content.ReadFromJsonAsync<BankStatement>())!;

        Reconciliation rec = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/reconciliations",
                new StartReconciliationRequest(statement.Id))).Content.ReadFromJsonAsync<Reconciliation>())!;

        ReconciliationWorksheet before = (await clerk.GetFromJsonAsync<ReconciliationWorksheet>($"/clients/{clientId}/reconciliations/{rec.Id}"))!;
        Assert.Equal(2, before.Entries.Count);
        Assert.False(before.Balanced);

        // complete is refused while unbalanced.
        Assert.Equal(HttpStatusCode.Conflict, (await clerk.PostAsync($"/clients/{clientId}/reconciliations/{rec.Id}/complete", null)).StatusCode);

        // Clear both entries → balanced.
        Guid[] entryIds = before.Entries.Select(e => e.EntryId).ToArray();
        ReconciliationWorksheet after = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/reconciliations/{rec.Id}/clear",
                new ClearRequest(entryIds))).Content.ReadFromJsonAsync<ReconciliationWorksheet>())!;
        Assert.Equal(60m, after.ClearedTotal);
        Assert.Equal(0m, after.ReconciledDifference);
        Assert.True(after.Balanced);

        // complete now succeeds.
        Reconciliation done = (await (await clerk.PostAsync($"/clients/{clientId}/reconciliations/{rec.Id}/complete", null))
            .Content.ReadFromJsonAsync<Reconciliation>())!;
        Assert.Equal(ReconciliationStatus.Completed, done.Status);
    }

    [Fact]
    public async Task A_statement_that_does_not_foot_is_rejected_422()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId, fixture);

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/bank-statements",
            new RecordBankStatementRequest(fixture.CashAccountId, new DateOnly(2026, 1, 31), 0m, 999m,
                [new BankStatementLineRequest(new DateOnly(2026, 1, 20), 100m, "deposit", null)]));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }

    [Fact]
    public async Task Auto_match_proposes_then_applies_to_balance_the_reconciliation()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId, fixture);
        DateOnly date = new(2026, 1, 20);
        DateOnly stmtDate = new(2026, 1, 31);

        // Real deposit (Dr Cash 100) + disbursement (Cr Cash 40), both approved → posted.
        CashDeposit dep = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/cash-deposits",
                new RecordCashDepositRequest([new CashLineRequest(fixture.MembersCapitalAccountId, 100m)], date, "DEP", null)))
            .Content.ReadFromJsonAsync<CashDeposit>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, dep.Id);
        CashDisbursement dis = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/cash-disbursements",
                new RecordCashDisbursementRequest([new CashLineRequest(fixture.InterestExpenseAccountId, 40m)], date, "DIS", null)))
            .Content.ReadFromJsonAsync<CashDisbursement>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, dis.Id);

        BankStatement statement = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bank-statements",
                new RecordBankStatementRequest(fixture.CashAccountId, stmtDate, 0m, 60m,
                    [new BankStatementLineRequest(date, 100m, "deposit", null), new BankStatementLineRequest(date, -40m, "payment", null)])))
            .Content.ReadFromJsonAsync<BankStatement>())!;
        Reconciliation rec = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/reconciliations",
                new StartReconciliationRequest(statement.Id))).Content.ReadFromJsonAsync<Reconciliation>())!;

        // Preview: proposes both pairings, nothing unmatched, mutates nothing.
        AutoMatchProposal proposal = (await (await clerk.PostAsync($"/clients/{clientId}/reconciliations/{rec.Id}/auto-match", null))
            .Content.ReadFromJsonAsync<AutoMatchProposal>())!;
        Assert.Equal(2, proposal.Matches.Count);
        Assert.Empty(proposal.UnmatchedStatementLines);
        Assert.Empty(proposal.UnmatchedEntries);

        // Preview left the reconciliation untouched — nothing cleared yet.
        ReconciliationWorksheet preview = (await clerk.GetFromJsonAsync<ReconciliationWorksheet>($"/clients/{clientId}/reconciliations/{rec.Id}"))!;
        Assert.All(preview.Entries, e => Assert.False(e.Cleared));

        // Apply: clears the matches → balanced.
        ReconciliationWorksheet applied = (await (await clerk.PostAsync($"/clients/{clientId}/reconciliations/{rec.Id}/auto-match?apply=true", null))
            .Content.ReadFromJsonAsync<ReconciliationWorksheet>())!;
        Assert.Equal(60m, applied.ClearedTotal);
        Assert.Equal(0m, applied.ReconciledDifference);
        Assert.True(applied.Balanced);

        // complete now succeeds.
        Reconciliation done = (await (await clerk.PostAsync($"/clients/{clientId}/reconciliations/{rec.Id}/complete", null))
            .Content.ReadFromJsonAsync<Reconciliation>())!;
        Assert.Equal(ReconciliationStatus.Completed, done.Status);
    }

    [Fact]
    public async Task A_fee_adjustment_posts_pending_then_clears_the_residual_after_approval()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId, fixture);
        DateOnly date = new(2026, 1, 20);
        DateOnly stmtDate = new(2026, 1, 31);

        // A real deposit (Dr Cash 100), approved → posted. Book cash = 100.
        CashDeposit dep = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/cash-deposits",
                new RecordCashDepositRequest([new CashLineRequest(fixture.MembersCapitalAccountId, 100m)], date, "DEP", null)))
            .Content.ReadFromJsonAsync<CashDeposit>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, dep.Id);

        // Statement foots WITH a $5 bank fee the books lack: 0 + 100 − 5 = 95.
        BankStatement statement = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bank-statements",
                new RecordBankStatementRequest(fixture.CashAccountId, stmtDate, 0m, 95m,
                    [new BankStatementLineRequest(date, 100m, "deposit", null), new BankStatementLineRequest(stmtDate, -5m, "service fee", null)])))
            .Content.ReadFromJsonAsync<BankStatement>())!;
        Reconciliation rec = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/reconciliations",
                new StartReconciliationRequest(statement.Id))).Content.ReadFromJsonAsync<Reconciliation>())!;

        // Clear only the deposit → a −5 fee residual remains.
        ReconciliationWorksheet beforeAdj = (await clerk.GetFromJsonAsync<ReconciliationWorksheet>($"/clients/{clientId}/reconciliations/{rec.Id}"))!;
        Guid depEntryId = beforeAdj.Entries.Single().EntryId;
        await clerk.PostAsJsonAsync($"/clients/{clientId}/reconciliations/{rec.Id}/clear", new ClearRequest([depEntryId]));
        ReconciliationWorksheet residual = (await clerk.GetFromJsonAsync<ReconciliationWorksheet>($"/clients/{clientId}/reconciliations/{rec.Id}"))!;
        Assert.Equal(-5m, residual.ReconciledDifference);
        Assert.False(residual.Balanced);

        // Record a Charge adjustment (Dr InterestExpense / Cr Cash 5) → 201; its entry is PendingApproval, ViaModule=reconciliation.
        BankAdjustment adj = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/reconciliations/{rec.Id}/adjustments",
                new RecordAdjustmentRequest(fixture.InterestExpenseAccountId, 5m, AdjustmentKind.Charge, null, "service fee")))
            .Content.ReadFromJsonAsync<BankAdjustment>())!;
        EntryResponse[] adjEntries = (await clerk.GetFromJsonAsync<EntryResponse[]>($"/clients/{clientId}/entries?sourceRef={adj.Id}"))!;
        Assert.Single(adjEntries);
        Assert.Equal("PendingApproval", adjEntries[0].Posting);
        Assert.Equal("reconciliation", adjEntries[0].ViaModule);

        // Approve it (distinct Approver — maker-checker), then it becomes an eligible cash entry.
        await ApproveBySourceRefAsync(clerk, approver, clientId, adj.Id);

        ReconciliationWorksheet afterApprove = (await clerk.GetFromJsonAsync<ReconciliationWorksheet>($"/clients/{clientId}/reconciliations/{rec.Id}"))!;
        Guid adjEntryId = afterApprove.Entries.Single(e => !e.Cleared).EntryId;
        ReconciliationWorksheet balanced = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/reconciliations/{rec.Id}/clear",
                new ClearRequest([adjEntryId]))).Content.ReadFromJsonAsync<ReconciliationWorksheet>())!;
        Assert.Equal(0m, balanced.ReconciledDifference);
        Assert.True(balanced.Balanced);

        Reconciliation done = (await (await clerk.PostAsync($"/clients/{clientId}/reconciliations/{rec.Id}/complete", null))
            .Content.ReadFromJsonAsync<Reconciliation>())!;
        Assert.Equal(ReconciliationStatus.Completed, done.Status);
    }

    [Fact]
    public async Task A_pending_adjustment_can_be_voided()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId, fixture);
        DateOnly stmtDate = new(2026, 1, 31);

        BankStatement statement = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bank-statements",
                new RecordBankStatementRequest(fixture.CashAccountId, stmtDate, 0m, -5m,
                    [new BankStatementLineRequest(stmtDate, -5m, "service fee", null)])))
            .Content.ReadFromJsonAsync<BankStatement>())!;
        Reconciliation rec = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/reconciliations",
                new StartReconciliationRequest(statement.Id))).Content.ReadFromJsonAsync<Reconciliation>())!;

        BankAdjustment adj = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/reconciliations/{rec.Id}/adjustments",
                new RecordAdjustmentRequest(fixture.InterestExpenseAccountId, 5m, AdjustmentKind.Charge, null, null)))
            .Content.ReadFromJsonAsync<BankAdjustment>())!;

        // Void the still-pending adjustment. The void calls the engine's entry-void, which requires Void
        // permission — drive it with the Approver (the SoD role that carries approve/void), not the Clerk.
        HttpResponseMessage voidResp = await approver.PostAsJsonAsync(
            $"/clients/{clientId}/reconciliations/{rec.Id}/adjustments/{adj.Id}/void", new Accounting101.Banking.Reconciliation.Api.VoidReasonRequest("recorded in error"));
        Assert.Equal(HttpStatusCode.OK, voidResp.StatusCode);
        BankAdjustment voided = (await voidResp.Content.ReadFromJsonAsync<BankAdjustment>())!;
        Assert.Equal(BankAdjustmentStatus.Void, voided.Status);
    }

    [Fact]
    public async Task An_adjustment_with_a_non_positive_amount_is_rejected_422()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId, fixture);
        DateOnly stmtDate = new(2026, 1, 31);

        BankStatement statement = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bank-statements",
                new RecordBankStatementRequest(fixture.CashAccountId, stmtDate, 0m, 10m,
                    [new BankStatementLineRequest(stmtDate, 10m, "deposit", null)])))
            .Content.ReadFromJsonAsync<BankStatement>())!;
        Reconciliation rec = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/reconciliations",
                new StartReconciliationRequest(statement.Id))).Content.ReadFromJsonAsync<Reconciliation>())!;

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/reconciliations/{rec.Id}/adjustments",
            new RecordAdjustmentRequest(fixture.InterestExpenseAccountId, 0m, AdjustmentKind.Charge, null, null));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }
}
