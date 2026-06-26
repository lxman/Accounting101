using Accounting101.Ledger.Contracts;

namespace Accounting101.Banking.Cash;

/// <summary>
/// Persists cash disbursements through the engine's document store as <em>evidentiary</em> data: a
/// disbursement is created mutable (draft), immediately finalized into an append-only numbered document,
/// and voidable. Number and status are derived from the engine's envelope, never stored. The module owns
/// no database connection.
/// </summary>
public sealed class DocumentCashDisbursementStore(IDocumentStore documents) : ICashDisbursementStore
{
    private const string Collection = "cash-disbursements";

    public async Task<CashDisbursement> RecordAsync(Guid clientId, CashDisbursementBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        // Evidentiary: create draft then immediately finalize to assign the sequence number.
        Guid id = await documents.CreateAsync(clientId, Collection, body, Tags(), ct);
        await documents.FinalizeAsync(clientId, Collection, id, ct);
        DocumentResult<CashDisbursementBody>? result = await documents.GetAsync<CashDisbursementBody>(clientId, Collection, id, ct);
        return Map(result!);
    }

    public Task VoidAsync(Guid clientId, Guid id, CancellationToken ct = default) =>
        documents.VoidAsync(clientId, Collection, id, ct);

    public async Task<CashDisbursement?> GetAsync(Guid clientId, Guid id, CancellationToken ct = default)
    {
        DocumentResult<CashDisbursementBody>? result = await documents.GetAsync<CashDisbursementBody>(clientId, Collection, id, ct);
        return result is null ? null : Map(result);
    }

    public async Task<IReadOnlyList<CashDisbursement>> GetByClientAsync(Guid clientId, CancellationToken ct = default)
    {
        IReadOnlyList<DocumentResult<CashDisbursementBody>> results =
            await documents.QueryAsync<CashDisbursementBody>(clientId, Collection, Tags(), ct);
        return results.Select(Map).ToList();
    }

    private static Dictionary<string, string> Tags() => new();

    private static CashDisbursement Map(DocumentResult<CashDisbursementBody> result) => new()
    {
        Id = result.Id,
        Number = result.Sequence is { } seq ? $"CD-{seq:D5}" : null,
        Lines = result.Body.Lines,
        Date = result.Body.Date,
        Reference = result.Body.Reference,
        Memo = result.Body.Memo,
        Status = result.State switch
        {
            DocumentLifecycle.Voided or DocumentLifecycle.Superseded => CashDisbursementStatus.Void,
            _ => CashDisbursementStatus.Posted,
        },
    };
}
