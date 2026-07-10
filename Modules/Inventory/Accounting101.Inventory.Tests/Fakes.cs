using Accounting101.Ledger.Contracts;

namespace Accounting101.Inventory.Tests;

/// <summary>In-memory stand-in for the engine: records posts, tracks each entry's lines for a real
/// per-dimension fold, models approve/reverse/void, and resolves entries by source back-link. The fold
/// gates on posting state (Active + Posted, or +PendingApproval when includePending) so the value fold and
/// the quantity projection see a consistent posted/pending world; ApproveAll() flips pending → posted.</summary>
internal sealed class FakeLedgerClient : ILedgerClient
{
    private readonly Dictionary<Guid, EntryResponse> _entries = new();
    private readonly Dictionary<Guid, IReadOnlyList<PostLineRequest>> _linesById = new();
    private readonly List<PostEntryRequest> _posted = [];

    public IReadOnlyList<PostEntryRequest> Posted => _posted;
    public bool ReversedOrWithdrawn { get; private set; }
    public bool ReturnNoEntries { get; set; }

    public Task<PostEntryResponse> PostAsync(Guid clientId, PostEntryRequest entry, CancellationToken cancellationToken = default)
    {
        _posted.Add(entry);
        var id = Guid.NewGuid();
        _entries[id] = Entry(id, entry.SourceRef, entry.SourceType, posting: "PendingApproval", reversalOf: null, lines: entry.Lines);
        _linesById[id] = entry.Lines;
        return Task.FromResult(new PostEntryResponse(id, "Active", "PendingApproval"));
    }

    /// <summary>Test helper: approve every pending entry so posted-only reads see them.</summary>
    public void ApproveAll()
    {
        foreach (Guid id in _entries.Keys.ToList())
            if (_entries[id].Posting == "PendingApproval")
                _entries[id] = _entries[id] with { Posting = "Posted" };
    }

    public Task<EntryResponse> ReverseAsync(Guid clientId, Guid entryId, ReverseRequest request, CancellationToken cancellationToken = default)
    {
        ReversedOrWithdrawn = true;
        EntryResponse original = _entries[entryId];
        var id = Guid.NewGuid();
        // Negated lines, posted immediately so the pair nets to zero under a posted-only fold; the original
        // stays Active (still counted) exactly like the real engine.
        IReadOnlyList<PostLineRequest> reversedLines = _linesById.TryGetValue(entryId, out IReadOnlyList<PostLineRequest>? originalLines)
            ? originalLines.Select(l => l with { Direction = Flip(l.Direction) }).ToList()
            : [];
        EntryResponse reversal = Entry(id, original.SourceRef, original.SourceType, posting: "Posted", reversalOf: entryId, lines: reversedLines);
        _entries[id] = reversal;
        _linesById[id] = reversedLines;
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

    public Task<IReadOnlyList<EntryResponse>> GetEntriesBySourceRefsAsync(Guid clientId, IReadOnlyList<Guid> sourceRefs, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<EntryResponse>>(ReturnNoEntries
            ? []
            : _entries.Values.Where(e => e.SourceRef is { } s && sourceRefs.Contains(s)).ToList());

    public Task<IReadOnlyList<SubledgerLineResponse>> GetSubledgerAsync(
        Guid clientId, Guid account, string dimension, DateOnly? asOf, CancellationToken cancellationToken = default,
        bool includePending = false)
    {
        Dictionary<Guid, decimal> totals = new();
        foreach ((Guid id, EntryResponse response) in _entries)
        {
            if (response.Status != "Active") continue;
            if (!includePending && response.Posting != "Posted") continue;
            if (!_linesById.TryGetValue(id, out IReadOnlyList<PostLineRequest>? lines)) continue;
            foreach (PostLineRequest line in lines)
            {
                if (line.AccountId != account) continue;
                if (line.Dimensions is null || !line.Dimensions.TryGetValue(dimension, out Guid dimValue)) continue;
                decimal signed = line.Direction == "Debit" ? line.Amount : -line.Amount;
                totals[dimValue] = totals.GetValueOrDefault(dimValue) + signed;
            }
        }
        return Task.FromResult<IReadOnlyList<SubledgerLineResponse>>(
            totals.Select(kv => new SubledgerLineResponse(account, kv.Key, kv.Value)).ToList());
    }

    private static string Flip(string direction) => direction == "Debit" ? "Credit" : "Debit";

    private static EntryResponse Entry(
        Guid id, Guid? sourceRef, string? sourceType, string posting, Guid? reversalOf,
        IReadOnlyList<PostLineRequest>? lines = null)
    {
        IReadOnlyList<EntryLineResponse> mapped = (lines ?? []).Select(l =>
            new EntryLineResponse(l.AccountId, l.Direction, l.Amount, l.Dimensions ?? new Dictionary<string, Guid>(), null)).ToList();
        return new(id, 0, default, "Standard", "Active", posting, mapped.Count, null, null, reversalOf, null, mapped, sourceRef, sourceType);
    }
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
