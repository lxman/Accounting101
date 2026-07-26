using Accounting101.Ledger.Contracts;

namespace Accounting101.Banking.Reconciliation;

/// <summary>Records bank-only adjustments against a reconciliation and posts them PendingApproval through
/// module identity; voids them (reverse if posted, withdraw if pending). The module never self-approves.</summary>
public sealed class AdjustmentService(
    IReconciliationStore reconciliations, IBankAdjustmentStore adjustments, ILedgerClient ledger)
{
    public async Task<(BankAdjustment Adjustment, bool Replayed)> RecordAdjustmentAsync(
        Guid clientId, Guid reconciliationId, RecordAdjustmentInput input, Guid? retryKey = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        Guid id = retryKey ?? Guid.NewGuid();
        BankAdjustment? known = retryKey is not null ? await adjustments.GetAsync(clientId, id, ct) : null;
        bool replayed = known is not null;

        // The reconciliation's cash account and default date are needed to compose the entry on every
        // call, replay or not — the same role Cash's/Payroll's posting-accounts fetch plays, not a
        // state-dependent guard. Its Completed status IS state that can change between the original
        // call and a later replay, so that check is gated to the create branch below.
        Reconciliation reconciliation = await reconciliations.GetAsync(clientId, reconciliationId, ct)
            ?? throw new InvalidOperationException($"Reconciliation {reconciliationId} not found.");
        if (!replayed && reconciliation.Status == ReconciliationStatus.Completed)
            throw new InvalidOperationException($"Reconciliation {reconciliationId} is already completed.");
        if (input.Amount <= 0m)
            throw new ArgumentException($"Adjustment amount must be positive; got {input.Amount}.");
        if (input.OffsetAccountId == reconciliation.CashAccountId)
            throw new ArgumentException("The offset account must differ from the cash account.");

        BankAdjustmentBody body = new(
            reconciliationId, reconciliation.CashAccountId, input.OffsetAccountId,
            input.Kind, input.Amount, input.Date ?? reconciliation.StatementDate, input.Memo);

        // 1. Persist the evidentiary document (finalized immediately — no draft phase).
        if (!replayed)
        {
            // The pre-check above is inherently TOCTOU under concurrency: two callers racing the same id
            // can both see "not found" and both reach here. The store's atomic create is the actual
            // arbiter — trust it over the pre-check for this response's status.
            (BankAdjustment Adjustment, bool Created) result = await adjustments.RecordAsync(clientId, id, body, ct);
            known = result.Adjustment;
            replayed = !result.Created;
        }

        // 2. Compose from the request and post on both branches — the ledger arbitrates a replay
        //    versus a key reused for different content, and a retry repairs a post that never landed.
        PostEntryRequest entry = AdjustmentPosting.Compose(id, body);
        // 3. Post PendingApproval — the module never approves its own entries.
        await ledger.PostAsync(clientId, entry, ct);
        return (known!, replayed);
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
