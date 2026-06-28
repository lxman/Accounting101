using Accounting101.Ledger.Contracts;

namespace Accounting101.Banking.Reconciliation;

/// <summary>Persists bank adjustments through the engine's document store as evidentiary data: created
/// mutable then immediately finalized into an append-only numbered document, and voidable. Number/status
/// derived from the envelope. Lists by reconciliation.</summary>
public sealed class DocumentBankAdjustmentStore(IDocumentStore documents) : IBankAdjustmentStore
{
    private const string Collection = "bank-adjustments";

    public async Task<BankAdjustment> RecordAsync(Guid clientId, BankAdjustmentBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        Guid id = await documents.CreateAsync(clientId, Collection, body, Tags(), ct);
        await documents.FinalizeAsync(clientId, Collection, id, ct);
        DocumentResult<BankAdjustmentBody>? result = await documents.GetAsync<BankAdjustmentBody>(clientId, Collection, id, ct);
        return Map(result!);
    }

    public Task VoidAsync(Guid clientId, Guid id, CancellationToken ct = default) =>
        documents.VoidAsync(clientId, Collection, id, ct);

    public async Task<BankAdjustment?> GetAsync(Guid clientId, Guid id, CancellationToken ct = default)
    {
        DocumentResult<BankAdjustmentBody>? result = await documents.GetAsync<BankAdjustmentBody>(clientId, Collection, id, ct);
        return result is null ? null : Map(result);
    }

    public async Task<IReadOnlyList<BankAdjustment>> GetByReconciliationAsync(Guid clientId, Guid reconciliationId, CancellationToken ct = default)
    {
        IReadOnlyList<DocumentResult<BankAdjustmentBody>> results =
            await documents.QueryAsync<BankAdjustmentBody>(clientId, Collection, Tags(), cancellationToken: ct);
        return results.Where(r => r.Body.ReconciliationId == reconciliationId).Select(Map).ToList();
    }

    private static Dictionary<string, string> Tags() => new();

    private static BankAdjustment Map(DocumentResult<BankAdjustmentBody> r) => new()
    {
        Id = r.Id,
        Number = r.Sequence is { } seq ? $"ADJ-{seq:D5}" : null,
        ReconciliationId = r.Body.ReconciliationId,
        CashAccountId = r.Body.CashAccountId,
        OffsetAccountId = r.Body.OffsetAccountId,
        Kind = r.Body.Kind,
        Amount = r.Body.Amount,
        Date = r.Body.Date,
        Memo = r.Body.Memo,
        Status = r.State is DocumentLifecycle.Voided or DocumentLifecycle.Superseded ? BankAdjustmentStatus.Void : BankAdjustmentStatus.Posted,
    };
}
