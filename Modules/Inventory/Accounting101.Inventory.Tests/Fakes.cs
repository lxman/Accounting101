using Accounting101.Ledger.Contracts;

namespace Accounting101.Inventory.Tests;

/// <summary>
/// An in-memory stand-in for the engine: records what was posted, models approve/reverse, and resolves
/// entries by their source back-link — enough to drive and assert the module's lifecycle without HTTP.
/// Copied verbatim from Accounting101.FixedAssets.Tests.Fakes.FakeLedgerClient.
/// </summary>
internal sealed class FakeLedgerClient : ILedgerClient
{
    private readonly Dictionary<Guid, EntryResponse> _entries = new();
    private readonly List<PostEntryRequest> _posted = [];

    public IReadOnlyList<PostEntryRequest> Posted => _posted;

    /// <summary>Flips true the moment either <see cref="ReverseAsync"/> or <see cref="VoidAsync"/> is called.</summary>
    public bool ReversedOrWithdrawn { get; private set; }

    /// <summary>When true, <see cref="GetEntriesBySourceRefAsync"/> returns an empty list — simulates a run
    /// stranded by a post that never landed.</summary>
    public bool ReturnNoEntries { get; set; }

    public Task<PostEntryResponse> PostAsync(Guid clientId, PostEntryRequest entry, CancellationToken cancellationToken = default)
    {
        _posted.Add(entry);
        var id = Guid.NewGuid();
        _entries[id] = Entry(id, entry.SourceRef, entry.SourceType, posting: "PendingApproval", reversalOf: null);
        return Task.FromResult(new PostEntryResponse(id, "Active", "PendingApproval"));
    }

    public Task<EntryResponse> ReverseAsync(Guid clientId, Guid entryId, ReverseRequest request, CancellationToken cancellationToken = default)
    {
        ReversedOrWithdrawn = true;
        EntryResponse original = _entries[entryId];
        var id = Guid.NewGuid();
        EntryResponse reversal = Entry(id, original.SourceRef, original.SourceType, posting: "PendingApproval", reversalOf: entryId);
        _entries[id] = reversal;
        return Task.FromResult(reversal);
    }

    public Task<EntryResponse> VoidAsync(Guid clientId, Guid entryId, VoidRequest request, CancellationToken cancellationToken = default)
    {
        ReversedOrWithdrawn = true;
        EntryResponse voided = _entries[entryId] with { Status = "Voided" };
        _entries[entryId] = voided;
        return Task.FromResult(voided);
    }

    public Task<IReadOnlyList<EntryResponse>> GetEntriesBySourceRefAsync(Guid clientId, Guid sourceRef, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<EntryResponse>>(ReturnNoEntries
            ? []
            : _entries.Values.Where(e => e.SourceRef == sourceRef).ToList());

    private static EntryResponse Entry(Guid id, Guid? sourceRef, string? sourceType, string posting, Guid? reversalOf) =>
        new(id, 0, default, "Standard", "Active", posting, 0, null, null, reversalOf, null, [], sourceRef, sourceType);
}

/// <summary>An in-memory item register: a dictionary of items keyed by id, enough to drive the movement
/// service's item resolution and valuation mutations.</summary>
internal sealed class InMemoryItemStore : IItemStore
{
    private readonly Dictionary<Guid, Item> _items = new();
    private readonly HashSet<Guid> _deactivated = [];

    public Task<Item> CreateAsync(Guid clientId, ItemBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        Item item = new()
        {
            Id = Guid.NewGuid(),
            Sku = body.Sku,
            Name = body.Name,
            Description = body.Description,
            UnitOfMeasure = body.UnitOfMeasure,
            Status = ItemStatus.Active,
            OnHandQuantity = 0m,
            TotalValue = 0m,
        };
        _items[item.Id] = item;
        return Task.FromResult(item);
    }

    public Task<UpdateResult> UpdateAsync(Guid clientId, Guid itemId, ItemBody body, CancellationToken ct = default) =>
        throw new NotSupportedException("Not needed by InventoryMovementServiceTests.");

    public Task<DeactivateResult> DeactivateAsync(Guid clientId, Guid itemId, CancellationToken ct = default)
    {
        if (!_items.ContainsKey(itemId)) return Task.FromResult(DeactivateResult.NotFound);
        return Task.FromResult(_deactivated.Add(itemId) ? DeactivateResult.Deactivated : DeactivateResult.AlreadyInactive);
    }

    public Task<ReactivateResult> ReactivateAsync(Guid clientId, Guid itemId, CancellationToken ct = default)
    {
        if (!_items.ContainsKey(itemId)) return Task.FromResult(ReactivateResult.NotFound);
        return Task.FromResult(_deactivated.Remove(itemId) ? ReactivateResult.Reactivated : ReactivateResult.AlreadyActive);
    }

    public Task<Item?> GetAsync(Guid clientId, Guid itemId, CancellationToken ct = default) =>
        Task.FromResult(_items.GetValueOrDefault(itemId));

    public Task<Item?> GetBySkuAsync(Guid clientId, string sku, CancellationToken ct = default) =>
        Task.FromResult(_items.Values.FirstOrDefault(i => i.Sku == sku));

    public Task<PagedResponse<Item>> GetByClientPagedAsync(
        Guid clientId, int skip, int limit, bool descending, bool includeInactive, CancellationToken ct = default)
    {
        IEnumerable<Item> all = _items.Values.Where(i => includeInactive || !_deactivated.Contains(i.Id));
        List<Item> ordered = (descending ? all.OrderByDescending(i => i.Sku) : all.OrderBy(i => i.Sku)).ToList();
        List<Item> items = ordered.Skip(Math.Max(0, skip)).Take(Math.Clamp(limit, 1, 200)).ToList();
        return Task.FromResult(new PagedResponse<Item>(items, ordered.Count, skip, limit));
    }

    /// <summary>Marks the item inactive without going through the deactivate lifecycle guard — a test-only
    /// helper for exercising the movement service's "inactive item" rejection directly.</summary>
    public void ForceInactive(Guid itemId)
    {
        if (_items.TryGetValue(itemId, out Item? item))
            _items[itemId] = item with { Status = ItemStatus.Inactive };
    }

    public Task SetValuationAsync(Guid clientId, Guid itemId, decimal onHand, decimal totalValue, CancellationToken ct = default)
    {
        if (_items.TryGetValue(itemId, out Item? item))
            _items[itemId] = item with { OnHandQuantity = onHand, TotalValue = totalValue };
        return Task.CompletedTask;
    }
}

/// <summary>An in-memory stock-movement store: assigns incrementing MV-##### numbers and filters voided
/// movements out of the per-item queries the movement service depends on.</summary>
internal sealed class InMemoryStockMovementStore : IStockMovementStore
{
    private readonly List<StockMovement> _movements = [];
    private int _next;

    public Task<StockMovement> RecordAsync(Guid clientId, StockMovementBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        StockMovement movement = new()
        {
            Id = Guid.NewGuid(),
            Number = $"MV-{Interlocked.Increment(ref _next):D5}",
            ItemId = body.ItemId,
            Type = body.Type,
            EffectiveDate = body.EffectiveDate,
            Memo = body.Memo,
            Quantity = body.Quantity,
            AppliedUnitCost = body.AppliedUnitCost,
            ExtendedCost = body.ExtendedCost,
            ResultingOnHand = body.ResultingOnHand,
            ResultingTotalValue = body.ResultingTotalValue,
            Status = MovementStatus.Posted,
        };
        _movements.Add(movement);
        return Task.FromResult(movement);
    }

    public Task VoidAsync(Guid clientId, Guid movementId, CancellationToken ct = default)
    {
        int index = _movements.FindIndex(m => m.Id == movementId);
        if (index >= 0) _movements[index] = _movements[index] with { Status = MovementStatus.Void };
        return Task.CompletedTask;
    }

    public Task<StockMovement?> GetAsync(Guid clientId, Guid movementId, CancellationToken ct = default) =>
        Task.FromResult(_movements.FirstOrDefault(m => m.Id == movementId));

    public Task<PagedResponse<StockMovement>> GetByItemPagedAsync(
        Guid clientId, Guid itemId, int skip, int limit, bool descending, bool includeVoided, CancellationToken ct = default)
    {
        IEnumerable<StockMovement> all = _movements.Where(m =>
            m.ItemId == itemId && (includeVoided || m.Status != MovementStatus.Void));
        List<StockMovement> ordered = (descending ? all.OrderByDescending(m => m.Number) : all.OrderBy(m => m.Number)).ToList();
        List<StockMovement> items = ordered.Skip(Math.Max(0, skip)).Take(Math.Clamp(limit, 1, 200)).ToList();
        return Task.FromResult(new PagedResponse<StockMovement>(items, ordered.Count, skip, limit));
    }

    public Task<StockMovement?> GetLatestForItemAsync(Guid clientId, Guid itemId, CancellationToken ct = default) =>
        Task.FromResult(_movements
            .Where(m => m.ItemId == itemId && m.Status != MovementStatus.Void)
            .OrderByDescending(m => m.Number)
            .FirstOrDefault());
}

/// <summary>Fixed set of posting accounts, exposed as public properties for test assertions.</summary>
internal sealed class FixedInventoryAccountsProvider : IInventoryAccountsProvider
{
    public Guid InventoryAssetAccountId { get; } = Guid.NewGuid();
    public Guid CogsAccountId { get; } = Guid.NewGuid();
    public Guid GrniClearingAccountId { get; } = Guid.NewGuid();
    public Guid InventoryAdjustmentAccountId { get; } = Guid.NewGuid();

    public Task<InventoryPostingAccounts> GetAccountsAsync(Guid clientId, CancellationToken ct = default) =>
        Task.FromResult(new InventoryPostingAccounts
        {
            InventoryAssetAccountId = InventoryAssetAccountId,
            CogsAccountId = CogsAccountId,
            GrniClearingAccountId = GrniClearingAccountId,
            InventoryAdjustmentAccountId = InventoryAdjustmentAccountId,
        });
}
