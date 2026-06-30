using Accounting101.Ledger.Contracts;

namespace Accounting101.Payables;

/// <summary>
/// Persists bills through the engine's document store using a two-tier split:
/// <list type="bullet">
///   <item><b>bill-drafts</b> (plain) — freely editable and discardable scratch copies; never part of the
///   evidentiary record.</item>
///   <item><b>bills</b> (evidentiary) — append-only numbered documents; entered only on enter (via
///   <see cref="PromoteDraftAsync"/>). Number and status are derived from the engine envelope, never stored.</item>
/// </list>
/// The module owns no database connection; the engine's <see cref="IDocumentStore"/> is the only dependency.
/// </summary>
public sealed class DocumentBillStore(IDocumentStore documents) : IBillStore
{
    private const string Drafts = "bill-drafts";     // plain
    private const string Collection = "bills";       // evidentiary

    public async Task<Bill> CreateDraftAsync(Guid clientId, BillBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        Guid id = Guid.NewGuid();
        await documents.PutAsync(clientId, Drafts, id, body, Tags(body.VendorId), ct);
        DocumentResult<BillBody>? r = await documents.GetAsync<BillBody>(clientId, Drafts, id, ct);
        return Map(r!);
    }

    public async Task<Bill> UpdateDraftAsync(Guid clientId, Guid billId, BillBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (await documents.GetAsync<BillBody>(clientId, Drafts, billId, ct) is null)
            throw new InvalidOperationException($"Bill {billId} is not an editable draft.");
        await documents.PutAsync(clientId, Drafts, billId, body, Tags(body.VendorId), ct);
        DocumentResult<BillBody>? r = await documents.GetAsync<BillBody>(clientId, Drafts, billId, ct);
        return Map(r!);
    }

    public async Task DiscardDraftAsync(Guid clientId, Guid billId, CancellationToken ct = default)
    {
        if (await documents.GetAsync<BillBody>(clientId, Drafts, billId, ct) is null)
            throw new InvalidOperationException($"Bill {billId} is not a discardable draft.");
        await documents.DeleteAsync(clientId, Drafts, billId, ct);
    }

    public async Task<Bill> PromoteDraftAsync(Guid clientId, Guid billId, CancellationToken ct = default)
    {
        DocumentResult<BillBody>? draft = await documents.GetAsync<BillBody>(clientId, Drafts, billId, ct)
            ?? throw new InvalidOperationException($"Bill {billId} is not a draft awaiting enter.");
        Guid enteredId = await documents.CreateAsync(clientId, Collection, draft.Body, Tags(draft.Body.VendorId), ct);
        await documents.FinalizeAsync(clientId, Collection, enteredId, ct);
        await documents.DeleteAsync(clientId, Drafts, billId, ct);
        DocumentResult<BillBody>? entered = await documents.GetAsync<BillBody>(clientId, Collection, enteredId, ct);
        return Map(entered!);
    }

    public Task VoidAsync(Guid clientId, Guid billId, CancellationToken ct = default) =>
        documents.VoidAsync(clientId, Collection, billId, ct);

    public async Task<Bill?> GetAsync(Guid clientId, Guid billId, CancellationToken ct = default)
    {
        DocumentResult<BillBody>? draft = await documents.GetAsync<BillBody>(clientId, Drafts, billId, ct);
        if (draft is not null) return Map(draft);
        DocumentResult<BillBody>? entered = await documents.GetAsync<BillBody>(clientId, Collection, billId, ct);
        return entered is null ? null : Map(entered);
    }

    public async Task<IReadOnlyList<Bill>> GetByVendorAsync(Guid clientId, Guid vendorId, CancellationToken ct = default)
    {
        IReadOnlyList<DocumentResult<BillBody>> drafts = await documents.QueryAsync<BillBody>(clientId, Drafts, Tags(vendorId), cancellationToken: ct);
        IReadOnlyList<DocumentResult<BillBody>> entered = await documents.QueryAsync<BillBody>(clientId, Collection, Tags(vendorId), cancellationToken: ct);
        return drafts.Concat(entered).Select(Map).ToList();
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
