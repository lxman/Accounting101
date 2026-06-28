using Accounting101.Ledger.Contracts;

namespace Accounting101.Banking.Reconciliation;

/// <summary>Records bank-only adjustments against a reconciliation and posts them PendingApproval through
/// module identity; voids them (reverse if posted, withdraw if pending). The module never self-approves.</summary>
public sealed class AdjustmentService(
    IReconciliationStore reconciliations, IBankAdjustmentStore adjustments, ILedgerClient ledger)
{
    public async Task<BankAdjustment> RecordAdjustmentAsync(
        Guid clientId, Guid reconciliationId, RecordAdjustmentInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        Reconciliation reconciliation = await reconciliations.GetAsync(clientId, reconciliationId, ct)
            ?? throw new InvalidOperationException($"Reconciliation {reconciliationId} not found.");
        if (reconciliation.Status == ReconciliationStatus.Completed)
            throw new InvalidOperationException($"Reconciliation {reconciliationId} is already completed.");
        if (input.Amount <= 0m)
            throw new ArgumentException($"Adjustment amount must be positive; got {input.Amount}.");
        if (input.OffsetAccountId == reconciliation.CashAccountId)
            throw new ArgumentException("The offset account must differ from the cash account.");

        BankAdjustmentBody body = new(
            reconciliationId, reconciliation.CashAccountId, input.OffsetAccountId,
            input.Kind, input.Amount, input.Date ?? reconciliation.StatementDate, input.Memo);

        BankAdjustment adjustment = await adjustments.RecordAsync(clientId, body, ct);
        PostEntryRequest entry = AdjustmentPosting.Compose(adjustment.Id, body);
        await ledger.PostAsync(clientId, entry, ct);   // PendingApproval — module never approves
        return adjustment;
    }

    public async Task<BankAdjustment> VoidAdjustmentAsync(
        Guid clientId, Guid adjustmentId, string? reason = null, CancellationToken ct = default)
    {
        BankAdjustment adjustment = await adjustments.GetAsync(clientId, adjustmentId, ct)
            ?? throw new InvalidOperationException($"Bank adjustment {adjustmentId} not found.");
        if (adjustment.Status != BankAdjustmentStatus.Posted)
            throw new InvalidOperationException($"Only a posted adjustment can be voided; {adjustmentId} is {adjustment.Status}.");

        IReadOnlyList<EntryResponse> spawned = await ledger.GetEntriesBySourceRefAsync(clientId, adjustmentId, ct);
        EntryResponse entry = spawned.FirstOrDefault(e => e is { Status: "Active", ReversalOf: null })
            ?? throw new InvalidOperationException($"No entry found for adjustment {adjustment.Number} to void.");

        if (entry.Posting == "Posted")
            await ledger.ReverseAsync(clientId, entry.Id, new ReverseRequest(adjustment.Date, reason ?? $"Voided bank adjustment {adjustmentId}"), ct);
        else
            await ledger.VoidAsync(clientId, entry.Id, new VoidRequest(reason ?? $"Voided bank adjustment {adjustmentId}"), ct);

        await adjustments.VoidAsync(clientId, adjustmentId, ct);
        return (await adjustments.GetAsync(clientId, adjustmentId, ct))!;
    }

    public Task<BankAdjustment?> GetAdjustmentAsync(Guid clientId, Guid adjustmentId, CancellationToken ct = default) =>
        adjustments.GetAsync(clientId, adjustmentId, ct);

    public Task<IReadOnlyList<BankAdjustment>> ListAdjustmentsAsync(Guid clientId, Guid reconciliationId, CancellationToken ct = default) =>
        adjustments.GetByReconciliationAsync(clientId, reconciliationId, ct);
}

/// <summary>Clerk-supplied inputs for a bank adjustment (cash account + default date come from the reconciliation).</summary>
public sealed record RecordAdjustmentInput(Guid OffsetAccountId, decimal Amount, AdjustmentKind Kind, DateOnly? Date, string? Memo);
