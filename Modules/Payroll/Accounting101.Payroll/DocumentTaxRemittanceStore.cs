using Accounting101.Ledger.Contracts;

namespace Accounting101.Payroll;

/// <summary>
/// Persists tax remittances through the engine's document store as <em>evidentiary</em> data: a
/// remittance is created mutable (draft), immediately finalized into an append-only numbered document,
/// and voidable. Number and status are derived from the engine's envelope, never stored. The module
/// owns no database connection.
/// </summary>
public sealed class DocumentTaxRemittanceStore(IDocumentStore documents) : ITaxRemittanceStore
{
    private const string Collection = "tax-remittances";

    public async Task<TaxRemittance> RecordAsync(Guid clientId, TaxRemittanceBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        // Evidentiary: create draft then immediately finalize to assign the sequence number.
        Guid id = await documents.CreateAsync(clientId, Collection, body, Tags(), ct);
        await documents.FinalizeAsync(clientId, Collection, id, ct);
        DocumentResult<TaxRemittanceBody>? result = await documents.GetAsync<TaxRemittanceBody>(clientId, Collection, id, ct);
        return Map(result!);
    }

    public Task VoidAsync(Guid clientId, Guid remittanceId, CancellationToken ct = default) =>
        documents.VoidAsync(clientId, Collection, remittanceId, ct);

    public async Task<TaxRemittance?> GetAsync(Guid clientId, Guid remittanceId, CancellationToken ct = default)
    {
        DocumentResult<TaxRemittanceBody>? result = await documents.GetAsync<TaxRemittanceBody>(clientId, Collection, remittanceId, ct);
        return result is null ? null : Map(result);
    }

    public async Task<IReadOnlyList<TaxRemittance>> GetByClientAsync(Guid clientId, CancellationToken ct = default)
    {
        IReadOnlyList<DocumentResult<TaxRemittanceBody>> results =
            await documents.QueryAsync<TaxRemittanceBody>(clientId, Collection, Tags(), ct);
        return results.Select(Map).ToList();
    }

    private static Dictionary<string, string> Tags() => new();

    private static TaxRemittance Map(DocumentResult<TaxRemittanceBody> result) => new()
    {
        Id = result.Id,
        Number = result.Sequence is { } seq ? $"TR-{seq:D5}" : null,
        WithholdingsAmount = result.Body.WithholdingsAmount,
        TaxesAmount = result.Body.TaxesAmount,
        PayDate = result.Body.PayDate,
        Memo = result.Body.Memo,
        Status = result.State switch
        {
            DocumentLifecycle.Voided or DocumentLifecycle.Superseded => TaxRemittanceStatus.Void,
            _ => TaxRemittanceStatus.Posted,
        },
    };
}
