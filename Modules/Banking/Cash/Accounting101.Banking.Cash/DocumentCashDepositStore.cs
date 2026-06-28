using Accounting101.Ledger.Contracts;

namespace Accounting101.Banking.Cash;

/// <summary>
/// Persists cash deposits through the engine's document store as <em>evidentiary</em> data: a deposit
/// is created mutable (draft), immediately finalized into an append-only numbered document, and voidable.
/// Number and status are derived from the engine's envelope, never stored. The module owns no database
/// connection.
/// </summary>
public sealed class DocumentCashDepositStore(IDocumentStore documents) : ICashDepositStore
{
    private const string Collection = "cash-deposits";

    public async Task<CashDeposit> RecordAsync(Guid clientId, CashDepositBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        // Evidentiary: create draft then immediately finalize to assign the sequence number.
        Guid id = await documents.CreateAsync(clientId, Collection, body, Tags(), ct);
        await documents.FinalizeAsync(clientId, Collection, id, ct);
        DocumentResult<CashDepositBody>? result = await documents.GetAsync<CashDepositBody>(clientId, Collection, id, ct);
        return Map(result!);
    }

    public Task VoidAsync(Guid clientId, Guid id, CancellationToken ct = default) =>
        documents.VoidAsync(clientId, Collection, id, ct);

    public async Task<CashDeposit?> GetAsync(Guid clientId, Guid id, CancellationToken ct = default)
    {
        DocumentResult<CashDepositBody>? result = await documents.GetAsync<CashDepositBody>(clientId, Collection, id, ct);
        return result is null ? null : Map(result);
    }

    public async Task<IReadOnlyList<CashDeposit>> GetByClientAsync(Guid clientId, CancellationToken ct = default)
    {
        IReadOnlyList<DocumentResult<CashDepositBody>> results =
            await documents.QueryAsync<CashDepositBody>(clientId, Collection, Tags(), cancellationToken: ct);
        return results.Select(Map).ToList();
    }

    private static Dictionary<string, string> Tags() => new();

    private static CashDeposit Map(DocumentResult<CashDepositBody> result) => new()
    {
        Id = result.Id,
        Number = result.Sequence is { } seq ? $"CR-{seq:D5}" : null,
        Lines = result.Body.Lines,
        Date = result.Body.Date,
        Reference = result.Body.Reference,
        Memo = result.Body.Memo,
        Status = result.State switch
        {
            DocumentLifecycle.Voided or DocumentLifecycle.Superseded => CashDepositStatus.Void,
            _ => CashDepositStatus.Posted,
        },
    };
}
