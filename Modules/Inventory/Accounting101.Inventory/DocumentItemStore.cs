using Accounting101.Ledger.Contracts;

namespace Accounting101.Inventory;

/// <summary>Persists items through the engine's document store as reference data (mutable, audited,
/// deactivatable). Server-owned fields — OnHandQuantity and TotalValue — are stamped by the store: zero on
/// create, preserved on update, overwritten only via SetValuationAsync. Status is NOT stored in the document
/// body; it is derived from the document's DocumentLifecycle (Active/Inactive), so any Map that needs
/// Status must read it from the DocumentResult. The module speaks only IDocumentStore.</summary>
public sealed class DocumentItemStore(IDocumentStore documents) : IItemStore
{
    private const string Collection = "items";
    private static readonly Dictionary<string, string> NoTags = new();

    public async Task<Item> CreateAsync(Guid clientId, ItemBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        Guid id = Guid.NewGuid();
        ItemDocument doc = ToDocument(body, 0m, 0m);
        await documents.PutAsync(clientId, Collection, id, doc, NoTags, ct);
        // A freshly-put reference document is always Active.
        return Map(id, doc, ItemStatus.Active);
    }

    public async Task<UpdateResult> UpdateAsync(Guid clientId, Guid itemId, ItemBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        DocumentResult<ItemDocument>? existing = await documents.GetAsync<ItemDocument>(clientId, Collection, itemId, ct);
        if (existing is null) return UpdateResult.NotFound;
        if (existing.State == DocumentLifecycle.Inactive) return UpdateResult.Inactive; // sticky: reactivate first

        if (!string.Equals(body.Sku, existing.Body.Sku, StringComparison.Ordinal))
        {
            Item? bySku = await GetBySkuAsync(clientId, body.Sku, ct);
            if (bySku is not null && bySku.Id != itemId) return UpdateResult.DuplicateSku;
        }

        // Only the editable params change; the valuation fields are preserved (movements own them).
        ItemDocument doc = ToDocument(body, existing.Body.OnHandQuantity, existing.Body.TotalValue);
        await documents.PutAsync(clientId, Collection, itemId, doc, NoTags, ct);
        return UpdateResult.Updated(Map(itemId, doc, ItemStatus.Active));
    }

    public async Task<DeactivateResult> DeactivateAsync(Guid clientId, Guid itemId, CancellationToken ct = default)
    {
        DocumentResult<ItemDocument>? existing = await documents.GetAsync<ItemDocument>(clientId, Collection, itemId, ct);
        if (existing is null) return DeactivateResult.NotFound;
        if (existing.State == DocumentLifecycle.Inactive) return DeactivateResult.AlreadyInactive;
        if (existing.Body.OnHandQuantity != 0m) return DeactivateResult.HasStock;
        await documents.DeactivateAsync(clientId, Collection, itemId, ct);
        return DeactivateResult.Deactivated;
    }

    public async Task<ReactivateResult> ReactivateAsync(Guid clientId, Guid itemId, CancellationToken ct = default)
    {
        DocumentResult<ItemDocument>? existing = await documents.GetAsync<ItemDocument>(clientId, Collection, itemId, ct);
        if (existing is null) return ReactivateResult.NotFound;
        if (existing.State != DocumentLifecycle.Inactive) return ReactivateResult.AlreadyActive;
        // The engine has no explicit reactivate primitive; a Put on a reference doc rebuilds it Active
        // (ScopedDocumentStore.PutReferenceAsync always sets DocumentState.Active). Re-put the SAME body
        // (preserving server-owned valuation) so only the lifecycle flips.
        await documents.PutAsync(clientId, Collection, itemId, existing.Body, NoTags, ct);
        return ReactivateResult.Reactivated;
    }

    public async Task<Item?> GetAsync(Guid clientId, Guid itemId, CancellationToken ct = default)
    {
        DocumentResult<ItemDocument>? result = await documents.GetAsync<ItemDocument>(clientId, Collection, itemId, ct);
        return result is null ? null : Map(result.Id, result.Body, StatusOf(result));
    }

    public async Task<Item?> GetBySkuAsync(Guid clientId, string sku, CancellationToken ct = default)
    {
        // Unbounded query (no limit) — the engine clamps to 200. Acceptable for now; revisit if a client
        // ever exceeds 200 items (a dedicated indexed lookup would replace this scan).
        IReadOnlyList<DocumentResult<ItemDocument>> results = await documents.QueryAsync<ItemDocument>(
            clientId, Collection, NoTags, skip: null, limit: null, descending: true, includeVoided: true, ct);
        DocumentResult<ItemDocument>? match = results.FirstOrDefault(r => r.Body.Sku == sku);
        return match is null ? null : Map(match.Id, match.Body, StatusOf(match));
    }

    public async Task<PagedResponse<Item>> GetByClientPagedAsync(
        Guid clientId, int skip, int limit, bool descending, bool includeInactive, CancellationToken ct = default)
    {
        IReadOnlyList<DocumentResult<ItemDocument>> page =
            await documents.QueryAsync<ItemDocument>(clientId, Collection, NoTags, skip, limit, descending, includeInactive, ct);
        long total = await documents.CountAsync(clientId, Collection, NoTags, includeInactive, ct);
        return new PagedResponse<Item>(page.Select(r => Map(r.Id, r.Body, StatusOf(r))).ToList(), total, skip, limit);
    }

    public async Task SetValuationAsync(Guid clientId, Guid itemId, decimal onHand, decimal totalValue, CancellationToken ct = default)
    {
        DocumentResult<ItemDocument>? existing = await documents.GetAsync<ItemDocument>(clientId, Collection, itemId, ct);
        if (existing is null) return;
        ItemDocument doc = existing.Body with { OnHandQuantity = onHand, TotalValue = totalValue };
        await documents.PutAsync(clientId, Collection, itemId, doc, NoTags, ct);
    }

    private static ItemStatus StatusOf(DocumentResult<ItemDocument> result) =>
        result.State == DocumentLifecycle.Inactive ? ItemStatus.Inactive : ItemStatus.Active;

    private static ItemDocument ToDocument(ItemBody body, decimal onHand, decimal totalValue) =>
        new(body.Sku, body.Name, body.Description, body.UnitOfMeasure, onHand, totalValue);

    private static Item Map(Guid id, ItemDocument d, ItemStatus status) => new()
    {
        Id = id, Sku = d.Sku, Name = d.Name, Description = d.Description, UnitOfMeasure = d.UnitOfMeasure,
        Status = status, OnHandQuantity = d.OnHandQuantity, TotalValue = d.TotalValue,
    };
}
