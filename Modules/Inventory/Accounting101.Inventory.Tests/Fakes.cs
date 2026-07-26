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

    // Maps a SourceRef to the request that first posted under it, and to the entry that request minted —
    // mirrors the real engine's idempotent-post arbitration (LedgerEndpoints.PostEntry /
    // EntryComparison.SameFinancialContent): a second PostAsync carrying the SAME SourceRef is either a
    // replay of the same operation (return the existing entry, don't double-count the fold) or a key
    // reused for different content (422 in production; an exception here).
    private readonly Dictionary<Guid, PostEntryRequest> _bySourceRef = new();
    private readonly Dictionary<Guid, Guid> _entryIdBySourceRef = new();

    // PostAsync is now the target of a per-SourceRef check-then-act over plain (non-concurrent)
    // collections. This module's InMemoryStockMovementStore already guards its own mutable state with
    // a lock for exactly this reason (concurrent race-test racers); PostAsync gets the same minimal
    // treatment rather than a broader rewrite (see the 2026-07-26 toctou-status-code plan's Task 9
    // cross-cutting finding).
    private readonly object _postLock = new();

    public IReadOnlyList<PostEntryRequest> Posted => _posted;
    public bool ReversedOrWithdrawn { get; private set; }
    public bool ReturnNoEntries { get; set; }

    /// <summary>Call counters — test-only, for asserting a batch read is a CONSTANT number of ledger
    /// calls (not per-item). Do not affect behavior.</summary>
    public int GetSubledgerCallCount;
    public int GetEntriesBySourceRefsCallCount;

    public Task<PostEntryResponse> PostAsync(Guid clientId, PostEntryRequest entry, CancellationToken cancellationToken = default)
    {
        lock (_postLock)
        {
            if (entry.SourceRef is { } sourceRef && _bySourceRef.TryGetValue(sourceRef, out PostEntryRequest? seen))
            {
                // Production answers 422 when a key is reused for different content. Mirror that here, or every
                // divergent-retry test written against this fake passes vacuously.
                if (!SameContent(seen, entry))
                    throw new InvalidOperationException(
                        $"Entry for SourceRef {sourceRef} already exists with different content.");

                EntryResponse existing = _entries[_entryIdBySourceRef[sourceRef]];
                return Task.FromResult(new PostEntryResponse(existing.Id, existing.Status, existing.Posting));
            }

            _posted.Add(entry);
            var id = Guid.NewGuid();
            _entries[id] = Entry(id, entry.SourceRef, entry.SourceType, posting: "PendingApproval", reversalOf: null, lines: entry.Lines);
            _linesById[id] = entry.Lines;
            if (entry.SourceRef is { } sr)
            {
                _bySourceRef[sr] = entry;
                _entryIdBySourceRef[sr] = id;
            }
            return Task.FromResult(new PostEntryResponse(id, "Active", "PendingApproval"));
        }
    }

    // Matches EntryComparison.SameFinancialContent (Backend/Accounting101.Ledger.Core/Journal/
    // EntryComparison.cs) field-for-field, minus SourceRef (already guaranteed equal — the call site
    // above only reaches this after keying on a matching SourceRef). Reference/Memo are deliberately
    // NOT compared: production ignores them (EntryComparison.cs:5-7's own doc comment calls them
    // "descriptive", and the implementation never reads them), so a Memo/Reference-only retry is a
    // successful replay in production, not a 422 — comparing them here would make this fake STRICTER
    // than production, which is exactly the wrong direction for a fidelity fake.
    private static bool SameContent(PostEntryRequest a, PostEntryRequest b) =>
        a.EffectiveDate == b.EffectiveDate
        && a.Type == b.Type
        && a.SourceType == b.SourceType
        && a.Lines.Count == b.Lines.Count
        && a.Lines.Zip(b.Lines).All(p =>
            p.First.AccountId == p.Second.AccountId
            && p.First.Direction == p.Second.Direction
            && p.First.Amount == p.Second.Amount
            && SameDimensions(p.First.Dimensions, p.Second.Dimensions));

    // Ported from EntryComparison.SameDimensions (EntryComparison.cs:37-45), a value comparison (not
    // reference equality) — extended only for PostLineRequest.Dimensions being nullable, unlike
    // production's non-null Line.Dimensions; null and empty are treated as equivalent.
    private static bool SameDimensions(IReadOnlyDictionary<string, Guid>? x, IReadOnlyDictionary<string, Guid>? y)
    {
        int xCount = x?.Count ?? 0;
        int yCount = y?.Count ?? 0;
        if (xCount != yCount) return false;
        if (xCount == 0) return true;
        foreach (KeyValuePair<string, Guid> kv in x!)
            if (!y!.TryGetValue(kv.Key, out Guid v) || v != kv.Value) return false;
        return true;
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
        // Negated lines; the reversal is PendingApproval until approved (exactly like the real engine —
        // LedgerService.ReverseAsync) and the original stays Active. A posted-only fold does NOT roll back
        // until the reversal is approved (call ApproveAll to model that).
        IReadOnlyList<PostLineRequest> reversedLines = _linesById.TryGetValue(entryId, out IReadOnlyList<PostLineRequest>? originalLines)
            ? originalLines.Select(l => l with { Direction = Flip(l.Direction) }).ToList()
            : [];
        EntryResponse reversal = Entry(id, original.SourceRef, original.SourceType, posting: "PendingApproval", reversalOf: entryId, lines: reversedLines);
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

    public Task<IReadOnlyList<EntryResponse>> GetEntriesBySourceRefsAsync(Guid clientId, IReadOnlyList<Guid> sourceRefs, CancellationToken cancellationToken = default)
    {
        GetEntriesBySourceRefsCallCount++;
        return Task.FromResult<IReadOnlyList<EntryResponse>>(ReturnNoEntries
            ? []
            : _entries.Values.Where(e => e.SourceRef is { } s && sourceRefs.Contains(s)).ToList());
    }

    public Task<IReadOnlyList<SubledgerLineResponse>> GetSubledgerAsync(
        Guid clientId, Guid account, string dimension, DateOnly? asOf, CancellationToken cancellationToken = default,
        bool includePending = false)
    {
        GetSubledgerCallCount++;
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

    public Task<IReadOnlyList<AccountResponse>> GetAccountsAsync(Guid clientId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Not needed by this fake's consumers; ChartReadinessE2eTests exercises the real HTTP-backed engine.");

    public Task<CapabilitiesResponse> GetMyCapabilitiesAsync(Guid clientId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Not needed by this fake's consumers; ChartReadinessE2eTests exercises the real HTTP-backed engine.");

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
        // Valuation is derived on read (the service overlays the ledger fold); the store defaults it to 0.
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
}

/// <summary>An in-memory stock-movement store: assigns incrementing MV-##### numbers and filters voided
/// movements out of the per-item queries the movement service depends on.</summary>
internal sealed class InMemoryStockMovementStore : IStockMovementStore
{
    private readonly List<StockMovement> _movements = [];
    private int _next;

    public Task<(StockMovement Movement, bool Created)> RecordAsync(Guid clientId, Guid id, StockMovementBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        lock (_movements)
        {
            // Insert-only, mirroring the real store's atomic create: a racer that loses gets back the
            // winner's movement and an honest Created: false, rather than appending a second row.
            StockMovement? existing = _movements.FirstOrDefault(m => m.Id == id);
            if (existing is not null)
                return Task.FromResult((existing, false));

            StockMovement movement = new()
            {
                Id = id,
                Number = $"MV-{Interlocked.Increment(ref _next):D5}",
                ItemId = body.ItemId,
                Type = body.Type,
                EffectiveDate = body.EffectiveDate,
                Memo = body.Memo,
                Quantity = body.Quantity,
                AppliedUnitCost = body.AppliedUnitCost,
                ExtendedCost = body.ExtendedCost,
                Status = MovementStatus.Posted,
            };
            _movements.Add(movement);
            return Task.FromResult((movement, true));
        }
    }

    public Task VoidAsync(Guid clientId, Guid movementId, CancellationToken ct = default)
    {
        lock (_movements)
        {
            int index = _movements.FindIndex(m => m.Id == movementId);
            if (index >= 0) _movements[index] = _movements[index] with { Status = MovementStatus.Void };
            return Task.CompletedTask;
        }
    }

    public Task<StockMovement?> GetAsync(Guid clientId, Guid movementId, CancellationToken ct = default)
    {
        lock (_movements)
            return Task.FromResult(_movements.FirstOrDefault(m => m.Id == movementId));
    }

    public Task<PagedResponse<StockMovement>> GetByItemPagedAsync(
        Guid clientId, Guid itemId, int skip, int limit, bool descending, bool includeVoided, CancellationToken ct = default)
    {
        lock (_movements)
        {
            IEnumerable<StockMovement> all = _movements.Where(m =>
                m.ItemId == itemId && (includeVoided || m.Status != MovementStatus.Void));
            List<StockMovement> ordered = (descending ? all.OrderByDescending(m => m.Number) : all.OrderBy(m => m.Number)).ToList();
            List<StockMovement> items = ordered.Skip(Math.Max(0, skip)).Take(Math.Clamp(limit, 1, 200)).ToList();
            return Task.FromResult(new PagedResponse<StockMovement>(items, ordered.Count, skip, limit));
        }
    }

    public Task<StockMovement?> GetLatestForItemAsync(Guid clientId, Guid itemId, CancellationToken ct = default)
    {
        lock (_movements)
            return Task.FromResult(_movements
                .Where(m => m.ItemId == itemId && m.Status != MovementStatus.Void)
                .OrderByDescending(m => m.Number)
                .FirstOrDefault());
    }

    public Task<IReadOnlyList<StockMovement>> GetAllByItemAsync(Guid clientId, Guid itemId, CancellationToken ct = default)
    {
        lock (_movements)
            return Task.FromResult<IReadOnlyList<StockMovement>>(_movements.Where(m => m.ItemId == itemId).ToList());
    }

    public Task<IReadOnlyList<StockMovement>> GetAllByItemsAsync(Guid clientId, IReadOnlyList<Guid> itemIds, CancellationToken ct = default)
    {
        HashSet<Guid> ids = itemIds.ToHashSet();
        lock (_movements)
            return Task.FromResult<IReadOnlyList<StockMovement>>(_movements.Where(m => ids.Contains(m.ItemId)).ToList());
    }
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
