using Accounting101.Ledger.Contracts;

namespace Accounting101.Banking.Reconciliation.Tests;

public sealed class ReconciliationServiceTests
{
    private static readonly Guid Cash = Guid.NewGuid();
    private static readonly DateOnly StmtDate = new(2026, 1, 31);

    private static EntryResponse CashEntry(Guid id, string direction, decimal amount) =>
        new(id, 0, new DateOnly(2026, 1, 15), "Standard", "Active", "Posted", 2, null, null, null, null,
            [new EntryLineResponse(Cash, direction, amount, new Dictionary<string, Guid>(), null),
             new EntryLineResponse(Guid.NewGuid(), direction == "Debit" ? "Credit" : "Debit", amount, new Dictionary<string, Guid>(), null)],
            SourceType: "Test", Reference: "R");

    private static (ReconciliationService svc, InMemoryBankStatementStore stmts, InMemoryReconciliationStore recs)
        Build(IReadOnlyList<EntryResponse> entries, decimal bookBalance)
    {
        InMemoryBankStatementStore stmts = new();
        InMemoryReconciliationStore recs = new();
        return (new ReconciliationService(stmts, recs, new FakeLedgerReader(entries, bookBalance)), stmts, recs);
    }

    private static BankStatementBody StatementBody(decimal opening, decimal closing, params (decimal amt, string desc)[] lines) =>
        new(Cash, StmtDate, opening, closing, lines.Select(l => new BankStatementLine(StmtDate, l.amt, l.desc, null)).ToList());

    [Fact]
    public async Task A_statement_that_does_not_foot_is_rejected()
    {
        (ReconciliationService svc, _, _) = Build([], 0m);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.RecordStatementAsync(Guid.NewGuid(), StatementBody(0m, 999m, (100m, "dep"))));
    }

    [Fact]
    public async Task Clearing_the_matching_entries_balances_the_reconciliation()
    {
        Guid clientId = Guid.NewGuid();
        Guid dep = Guid.NewGuid(), pay = Guid.NewGuid();
        EntryResponse[] entries = [CashEntry(dep, "Debit", 100m), CashEntry(pay, "Credit", 40m)]; // net +60
        (ReconciliationService svc, _, _) = Build(entries, 60m);

        BankStatement statement = await svc.RecordStatementAsync(clientId, StatementBody(0m, 60m, (100m, "dep"), (-40m, "pay")));
        Reconciliation rec = await svc.StartReconciliationAsync(clientId, statement.Id);

        ReconciliationWorksheet before = (await svc.GetWorksheetAsync(clientId, rec.Id))!;
        Assert.False(before.Balanced);                    // nothing cleared yet
        Assert.Equal(60m, before.ReconciledDifference);   // 60 − (0 + 0)

        ReconciliationWorksheet after = await svc.ClearAsync(clientId, rec.Id, [dep, pay]);
        Assert.Equal(60m, after.ClearedTotal);
        Assert.Equal(0m, after.ReconciledDifference);
        Assert.True(after.Balanced);

        Reconciliation done = await svc.CompleteAsync(clientId, rec.Id);
        Assert.Equal(ReconciliationStatus.Completed, done.Status);
    }

    [Fact]
    public async Task A_bank_only_residual_leaves_a_non_zero_difference_and_blocks_complete()
    {
        Guid clientId = Guid.NewGuid();
        Guid dep = Guid.NewGuid();
        EntryResponse[] entries = [CashEntry(dep, "Debit", 100m)];
        (ReconciliationService svc, _, _) = Build(entries, 100m);

        // Bank closing 95: a $5 bank fee the books don't have. Statement foots (0 + 100 − 5 = 95).
        BankStatement statement = await svc.RecordStatementAsync(clientId, StatementBody(0m, 95m, (100m, "dep"), (-5m, "fee")));
        Reconciliation rec = await svc.StartReconciliationAsync(clientId, statement.Id);
        await svc.ClearAsync(clientId, rec.Id, [dep]);

        ReconciliationWorksheet ws = (await svc.GetWorksheetAsync(clientId, rec.Id))!;
        Assert.Equal(-5m, ws.ReconciledDifference);       // 95 − (0 + 100)
        Assert.False(ws.Balanced);
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CompleteAsync(clientId, rec.Id));
    }

    [Fact]
    public async Task Clearing_an_entry_not_on_the_cash_account_is_rejected()
    {
        Guid clientId = Guid.NewGuid();
        EntryResponse[] entries = [CashEntry(Guid.NewGuid(), "Debit", 100m)];
        (ReconciliationService svc, _, _) = Build(entries, 100m);
        BankStatement statement = await svc.RecordStatementAsync(clientId, StatementBody(0m, 100m, (100m, "dep")));
        Reconciliation rec = await svc.StartReconciliationAsync(clientId, statement.Id);

        await Assert.ThrowsAsync<ArgumentException>(() => svc.ClearAsync(clientId, rec.Id, [Guid.NewGuid()]));
    }

    [Fact]
    public async Task Auto_match_proposes_entries_by_amount_and_excludes_already_cleared()
    {
        Guid clientId = Guid.NewGuid();
        Guid dep = Guid.NewGuid(), pay = Guid.NewGuid();
        EntryResponse[] entries = [CashEntry(dep, "Debit", 100m), CashEntry(pay, "Credit", 40m)];
        (ReconciliationService svc, _, _) = Build(entries, 60m);

        BankStatement statement = await svc.RecordStatementAsync(clientId, StatementBody(0m, 60m, (100m, "dep"), (-40m, "pay")));
        Reconciliation rec = await svc.StartReconciliationAsync(clientId, statement.Id);

        // Nothing cleared yet → both lines pair to their entries.
        AutoMatchProposal full = await svc.AutoMatchAsync(clientId, rec.Id);
        Assert.Equal(2, full.Matches.Count);
        Assert.Empty(full.UnmatchedStatementLines);
        Assert.Equal(new[] { dep, pay }.Order(), full.MatchedEntryIds.Order());

        // Clear the deposit manually → auto-match now proposes only the payment.
        await svc.ClearAsync(clientId, rec.Id, [dep]);
        AutoMatchProposal partial = await svc.AutoMatchAsync(clientId, rec.Id);
        Assert.Single(partial.Matches);
        Assert.Equal(pay, partial.Matches[0].EntryId);
        Assert.DoesNotContain(partial.UnmatchedEntries, e => e.EntryId == dep); // cleared entry not offered
    }

    [Fact]
    public async Task Auto_match_apply_clears_the_matches_and_balances()
    {
        Guid clientId = Guid.NewGuid();
        Guid dep = Guid.NewGuid(), pay = Guid.NewGuid();
        EntryResponse[] entries = [CashEntry(dep, "Debit", 100m), CashEntry(pay, "Credit", 40m)];
        (ReconciliationService svc, _, _) = Build(entries, 60m);

        BankStatement statement = await svc.RecordStatementAsync(clientId, StatementBody(0m, 60m, (100m, "dep"), (-40m, "pay")));
        Reconciliation rec = await svc.StartReconciliationAsync(clientId, statement.Id);

        ReconciliationWorksheet after = await svc.AutoMatchApplyAsync(clientId, rec.Id);
        Assert.Equal(60m, after.ClearedTotal);
        Assert.Equal(0m, after.ReconciledDifference);
        Assert.True(after.Balanced);
        Assert.All(after.Entries, e => Assert.True(e.Cleared));
    }

    [Fact]
    public async Task Auto_match_on_a_completed_reconciliation_is_rejected()
    {
        Guid clientId = Guid.NewGuid();
        Guid dep = Guid.NewGuid();
        EntryResponse[] entries = [CashEntry(dep, "Debit", 100m)];
        (ReconciliationService svc, _, _) = Build(entries, 100m);

        BankStatement statement = await svc.RecordStatementAsync(clientId, StatementBody(0m, 100m, (100m, "dep")));
        Reconciliation rec = await svc.StartReconciliationAsync(clientId, statement.Id);
        await svc.AutoMatchApplyAsync(clientId, rec.Id);
        await svc.CompleteAsync(clientId, rec.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.AutoMatchAsync(clientId, rec.Id));
    }
}
