using Accounting101.Ledger.Contracts;

namespace Accounting101.Banking.Reconciliation;

/// <summary>The reconciliation lifecycle: record a bank statement, start a reconciliation against it,
/// clear/unclear ledger cash entries, and read the worksheet. Read-only on the ledger — no posting.</summary>
public sealed class ReconciliationService(
    IBankStatementStore statements, IReconciliationStore reconciliations, IReconciliationLedgerReader ledger)
{
    public async Task<BankStatement> RecordStatementAsync(Guid clientId, BankStatementBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (body.Lines.Count == 0)
            throw new ArgumentException("A bank statement needs at least one line.");
        decimal expectedClosing = body.OpeningBalance + body.Lines.Sum(l => l.Amount);
        if (expectedClosing != body.ClosingBalance)
            throw new ArgumentException(
                $"Statement does not foot: opening {body.OpeningBalance:C} + lines {body.Lines.Sum(l => l.Amount):C} = {expectedClosing:C}, but closing is {body.ClosingBalance:C}.");
        return await statements.RecordAsync(clientId, body, ct);
    }

    public Task<BankStatement?> GetStatementAsync(Guid clientId, Guid id, CancellationToken ct = default) =>
        statements.GetAsync(clientId, id, ct);

    public Task<IReadOnlyList<BankStatement>> ListStatementsAsync(Guid clientId, Guid cashAccountId, CancellationToken ct = default) =>
        statements.GetByAccountAsync(clientId, cashAccountId, ct);

    public async Task<Reconciliation> StartReconciliationAsync(Guid clientId, Guid bankStatementId, CancellationToken ct = default)
    {
        BankStatement statement = await statements.GetAsync(clientId, bankStatementId, ct)
            ?? throw new ArgumentException($"Bank statement {bankStatementId} does not exist.");
        return await reconciliations.CreateAsync(clientId, statement.CashAccountId, statement.Id, statement.StatementDate, ct);
    }

    public async Task<ReconciliationWorksheet?> GetWorksheetAsync(Guid clientId, Guid reconciliationId, CancellationToken ct = default)
    {
        Reconciliation? reconciliation = await reconciliations.GetAsync(clientId, reconciliationId, ct);
        if (reconciliation is null) return null;
        return await BuildWorksheetAsync(clientId, reconciliation, ct);
    }

    public async Task<ReconciliationWorksheet> ClearAsync(Guid clientId, Guid reconciliationId, IReadOnlyList<Guid> entryIds, CancellationToken ct = default)
    {
        Reconciliation reconciliation = await RequireOpenAsync(clientId, reconciliationId, ct);
        IReadOnlyList<EntryResponse> eligible = await EligibleEntriesAsync(clientId, reconciliation, ct);
        var eligibleIds = eligible.Select(e => e.Id).ToHashSet();
        foreach (Guid id in entryIds)
            if (!eligibleIds.Contains(id))
                throw new ArgumentException($"Entry {id} is not a posted entry on this cash account dated on or before the statement date.");

        var cleared = reconciliation.ClearedEntryIds.Concat(entryIds).Distinct().ToList();
        Reconciliation updated = reconciliation with { ClearedEntryIds = cleared };
        await reconciliations.SaveAsync(clientId, updated, ct);
        return BuildWorksheet(updated, await statements.GetAsync(clientId, updated.BankStatementId, ct)!, eligible, await ledger.GetCashBalanceAsync(clientId, updated.CashAccountId, updated.StatementDate, ct));
    }

    public async Task<ReconciliationWorksheet> UnclearAsync(Guid clientId, Guid reconciliationId, IReadOnlyList<Guid> entryIds, CancellationToken ct = default)
    {
        Reconciliation reconciliation = await RequireOpenAsync(clientId, reconciliationId, ct);
        var remove = entryIds.ToHashSet();
        var cleared = reconciliation.ClearedEntryIds.Where(id => !remove.Contains(id)).ToList();
        Reconciliation updated = reconciliation with { ClearedEntryIds = cleared };
        await reconciliations.SaveAsync(clientId, updated, ct);
        return await BuildWorksheetAsync(clientId, updated, ct);
    }

    public async Task<Reconciliation> CompleteAsync(Guid clientId, Guid reconciliationId, CancellationToken ct = default)
    {
        Reconciliation reconciliation = await RequireOpenAsync(clientId, reconciliationId, ct);
        ReconciliationWorksheet worksheet = await BuildWorksheetAsync(clientId, reconciliation, ct);
        if (!worksheet.Balanced)
            throw new InvalidOperationException(
                $"Cannot complete: the reconciliation is not balanced (difference {worksheet.ReconciledDifference:C}). Clear the remaining items or record the bank-only adjustments first.");
        Reconciliation completed = reconciliation with { Status = ReconciliationStatus.Completed };
        await reconciliations.SaveAsync(clientId, completed, ct);
        return completed;
    }

    private async Task<Reconciliation> RequireOpenAsync(Guid clientId, Guid reconciliationId, CancellationToken ct)
    {
        Reconciliation reconciliation = await reconciliations.GetAsync(clientId, reconciliationId, ct)
            ?? throw new InvalidOperationException($"Reconciliation {reconciliationId} not found.");
        if (reconciliation.Status == ReconciliationStatus.Completed)
            throw new InvalidOperationException($"Reconciliation {reconciliationId} is already completed.");
        return reconciliation;
    }

    /// <summary>Active, posted entries touching the cash account, dated on or before the statement date.</summary>
    private async Task<IReadOnlyList<EntryResponse>> EligibleEntriesAsync(Guid clientId, Reconciliation reconciliation, CancellationToken ct) =>
        (await ledger.GetEntriesTouchingAccountAsync(clientId, reconciliation.CashAccountId, ct))
            .Where(e => e.Status == "Active" && e.Posting == "Posted" && e.EffectiveDate <= reconciliation.StatementDate)
            .ToList();

    private async Task<ReconciliationWorksheet> BuildWorksheetAsync(Guid clientId, Reconciliation reconciliation, CancellationToken ct)
    {
        BankStatement statement = (await statements.GetAsync(clientId, reconciliation.BankStatementId, ct))!;
        IReadOnlyList<EntryResponse> eligible = await EligibleEntriesAsync(clientId, reconciliation, ct);
        decimal bookBalance = await ledger.GetCashBalanceAsync(clientId, reconciliation.CashAccountId, reconciliation.StatementDate, ct);
        return BuildWorksheet(reconciliation, statement, eligible, bookBalance);
    }

    private static ReconciliationWorksheet BuildWorksheet(
        Reconciliation reconciliation, BankStatement statement, IReadOnlyList<EntryResponse> eligible, decimal bookBalance)
    {
        var clearedIds = reconciliation.ClearedEntryIds.ToHashSet();
        List<WorksheetEntry> entries = eligible
            .Select(e => new WorksheetEntry(e.Id, e.EffectiveDate, e.Reference, e.SourceType,
                ReconciliationMath.CashEffect(e, reconciliation.CashAccountId), clearedIds.Contains(e.Id)))
            .ToList();
        decimal clearedTotal = ReconciliationMath.ClearedTotal(eligible, clearedIds, reconciliation.CashAccountId);
        decimal difference = ReconciliationMath.ReconciledDifference(statement.OpeningBalance, statement.ClosingBalance, clearedTotal);
        return new ReconciliationWorksheet(reconciliation, statement, entries, bookBalance, clearedTotal, difference, ReconciliationMath.IsBalanced(difference));
    }
}
