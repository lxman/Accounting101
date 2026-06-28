using Accounting101.Ledger.Contracts;

namespace Accounting101.Payables;

/// <summary>
/// Persists bills through the engine's document store as <em>evidentiary</em> data: a draft is created
/// mutable, finalized (entered) into an append-only numbered document, and voidable. Number and status are
/// derived from the engine's envelope, never stored. The module owns no database connection.
/// </summary>
public sealed class DocumentBillStore(IDocumentStore documents) : IBillStore
{
    private const string Collection = "bills";

    public async Task<Bill> CreateDraftAsync(Guid clientId, BillBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        Guid id = await documents.CreateAsync(clientId, Collection, body, Tags(body.VendorId), ct);
        DocumentResult<BillBody>? result = await documents.GetAsync<BillBody>(clientId, Collection, id, ct);
        return Map(result!);
    }

    public async Task<Bill> FinalizeAsync(Guid clientId, Guid billId, CancellationToken ct = default)
    {
        await documents.FinalizeAsync(clientId, Collection, billId, ct);
        DocumentResult<BillBody>? result = await documents.GetAsync<BillBody>(clientId, Collection, billId, ct);
        return Map(result!);
    }

    public Task VoidAsync(Guid clientId, Guid billId, CancellationToken ct = default) =>
        documents.VoidAsync(clientId, Collection, billId, ct);

    public async Task<Bill?> GetAsync(Guid clientId, Guid billId, CancellationToken ct = default)
    {
        DocumentResult<BillBody>? result = await documents.GetAsync<BillBody>(clientId, Collection, billId, ct);
        return result is null ? null : Map(result);
    }

    public async Task<IReadOnlyList<Bill>> GetByVendorAsync(Guid clientId, Guid vendorId, CancellationToken ct = default)
    {
        IReadOnlyList<DocumentResult<BillBody>> results =
            await documents.QueryAsync<BillBody>(clientId, Collection, Tags(vendorId), cancellationToken: ct);
        return results.Select(Map).ToList();
    }

    private static Dictionary<string, string> Tags(Guid vendorId) => new() { ["Vendor"] = vendorId.ToString() };

    private static Bill Map(DocumentResult<BillBody> result) => new()
    {
        Id = result.Id,
        VendorId = result.Body.VendorId,
        Number = result.Sequence is { } seq ? $"BILL-{seq:D5}" : null,
        BillDate = result.Body.BillDate,
        DueDate = result.Body.DueDate,
        VendorReference = result.Body.VendorReference,
        Memo = result.Body.Memo,
        Status = result.State switch
        {
            DocumentLifecycle.Finalized => BillStatus.Entered,
            DocumentLifecycle.Voided or DocumentLifecycle.Superseded => BillStatus.Void,
            _ => BillStatus.Draft,
        },
        Lines = result.Body.Lines
            .Select(l => new BillLine { Description = l.Description, Amount = l.Amount, ExpenseAccountId = l.ExpenseAccountId })
            .ToList(),
    };
}
