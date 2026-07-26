using Accounting101.Ledger.Contracts;

namespace Accounting101.Inventory;

/// <summary>Domain input for recording a stock movement. <see cref="EffectiveDate"/> is required — the
/// clerk always dates a movement (mirrors how FA runs take a period and cash vouchers take a date); the
/// engine forbids wall-clock reads in module code, so there is no "default to today" fallback.
/// <see cref="Id"/> is the optional caller-supplied retry key that becomes the movement's id.</summary>
public sealed record RecordMovement(
    Guid ItemId, MovementType Type, decimal Quantity, decimal? UnitCost, DateOnly EffectiveDate, string? Memo,
    Guid? Id = null);

/// <summary>Records a stock movement: validates shape, resolves the item, computes the costing effect
/// from the folded (pending-inclusive) valuation via <see cref="InventoryValuation"/>, persists the
/// numbered movement, and posts one balanced PendingApproval entry via <see cref="InventoryPosting"/>.
/// On-hand/value are never stamped onto the item — they are derived on read from the ledger fold +
/// movement projection. This is the module's first GL-posting service.</summary>
public sealed class InventoryMovementService(
    IItemStore items, IStockMovementStore movements, IInventoryAccountsProvider accounts, ILedgerClient ledger,
    ItemValuationService valuation)
{
    /// <summary>Records a movement, or REPLAYS one when the caller supplies a retry key that is already
    /// taken. The replay branch is wider here than on any other retry-key endpoint: this path derives what
    /// it posts from mutable state (the pending-inclusive fold), so re-running it on a replay would
    /// re-apply the movement's own effect to on-hand and value — a double-count the ledger's
    /// divergent-content 422 could NOT catch, because the composed entry would be identical.</summary>
    public async Task<(StockMovement Movement, bool Replayed)> RecordAsync(
        Guid clientId, RecordMovement request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 1. Shape validation (→ 422). Direction/cost rules per movement type. Pure — it reads only the
        // request, so it guards BOTH branches: a replay carrying a malformed request is still a 422.
        ValidateShape(request);

        Guid id = request.Id ?? Guid.NewGuid();
        StockMovement? known = request.Id is not null ? await movements.GetAsync(clientId, id, ct) : null;
        bool replayed = known is not null;

        // 2. The posting accounts are needed to compose the entry on EVERY call, replay or not — data
        // acquisition, not a state-dependent guard (the same role this fetch plays in Cash/Payroll/
        // Reconciliation). Resolving it here still honours the original ordering promise — a config error
        // fails before any side effect — and now does so a little earlier than before.
        InventoryPostingAccounts postingAccounts = await accounts.GetAccountsAsync(clientId, ct);

        // 3. The extended cost is DERIVED, not carried on the request: an issue/shrinkage costs at the
        // folded weighted average. On a replay it must come from the STORED movement — recomputing it from
        // a fold that already contains this movement yields a different cost (or zero on a fold that has
        // gone to zero on-hand, or a spurious block-negative 409), which would turn a legitimate retry into
        // a 422. Every other field of the entry is still composed from the REQUEST, so a key reused for
        // different financial content is still arbitrated by the ledger.
        decimal extendedCost = known?.ExtendedCost ?? 0m;

        if (!replayed)
        {
            // 4. Resolve item; must exist and be active. State-dependent — an item can be deactivated
            // between the original call and a later replay, and a replay must not be refused for that.
            Item item = await items.GetAsync(clientId, request.ItemId, ct)
                ?? throw new KeyNotFoundException($"Item {request.ItemId} not found.");
            if (item.Status != ItemStatus.Active)
                throw new InvalidOperationException("Item is inactive; reactivate it before recording movements.");

            // 5. Compute the effect (may throw InvalidOperationException for block-negative → 409).
            // Current valuation is the pending-inclusive fold (writes see pending claims), NOT the stored item.
            // State-dependent in the strongest sense: after the original call landed, this fold ALREADY
            // contains this movement.
            ItemValuation folded = await valuation.GetAsync(clientId, request.ItemId, includePending: true, ct);
            Valuation current = new(folded.OnHand, folded.TotalValue);
            MovementEffect effect = request.Type switch
            {
                MovementType.Receipt    => InventoryValuation.Receipt(current, request.Quantity, request.UnitCost!.Value),
                MovementType.Issue      => InventoryValuation.Issue(current, request.Quantity),
                MovementType.Adjustment => InventoryValuation.Adjustment(current, request.Quantity, request.UnitCost),
                _ => throw new ArgumentOutOfRangeException(nameof(request)),
            };

            // Reject a non-positive extended cost BEFORE any side effect — otherwise the movement would be
            // persisted before InventoryPosting rejects it, stranding an entry-less movement. Derived from
            // the fold above, so it is state-dependent too and belongs on this branch.
            if (effect.ExtendedCost <= 0m)
                throw new ArgumentException("Movement extended cost must be positive.");

            extendedCost = effect.ExtendedCost;

            // 6. Persist the numbered movement under the caller's id. No valuation snapshot is stored —
            // on-hand and value are derived on read from the ledger fold + movement projection.
            known = await movements.RecordAsync(clientId, id, new StockMovementBody(
                request.ItemId, request.Type, request.EffectiveDate, request.Memo,
                request.Quantity, effect.AppliedUnitCost, effect.ExtendedCost), ct);
        }

        // 7. Compose from the request and post one PendingApproval entry on BOTH branches — the ledger
        //    arbitrates a replay versus a key reused for different content, and a retry repairs a post that
        //    never landed. The entry's id is derived from the movement id, so a replay is idempotent there.
        PostEntryRequest entry = InventoryPosting.Compose(
            request.Type, request.Quantity, id, request.ItemId, extendedCost, request.EffectiveDate, request.Memo, postingAccounts);
        await ledger.PostAsync(clientId, entry, ct);

        return (known!, replayed);
    }

    /// <summary>Voids the most-recent stock movement for its item — LIFO enforced: only the latest
    /// non-void movement for the item may be voided, never an earlier one while a later one still stands.
    /// Reverses the movement's spawned entry if posted (or withdraws it if still pending; tolerates a
    /// stranded post with no entry at all), then flips the movement's Status to Void. There is NO manual
    /// valuation restore: the reversed/withdrawn entry and the now-void movement drop straight out of the
    /// ledger fold + quantity projection, so the item's on-hand/value roll back on the next read. Mirrors
    /// FixedAssetsRunService.VoidRunAsync.</summary>
    public async Task<StockMovement> VoidAsync(Guid clientId, Guid movementId, string? reason, CancellationToken ct = default)
    {
        StockMovement movement = await movements.GetAsync(clientId, movementId, ct)
            ?? throw new KeyNotFoundException($"Stock movement {movementId} not found.");
        if (movement.Status != MovementStatus.Posted)
            throw new InvalidOperationException($"Only a posted movement can be voided; {movementId} is {movement.Status}.");

        // LIFO — only the most recent non-voided movement FOR THIS ITEM may be voided.
        StockMovement? latest = await movements.GetLatestForItemAsync(clientId, movement.ItemId, ct);
        if (latest is null || latest.Id != movement.Id)
            throw new InvalidOperationException("Only the most recent movement for this item can be voided.");

        // Reverse the posted entry (or withdraw it if still pending). Tolerate a missing entry (stranded post).
        IReadOnlyList<EntryResponse> spawned = await ledger.GetEntriesBySourceRefAsync(clientId, movementId, ct);
        EntryResponse? entry = spawned.FirstOrDefault(e => e is { Status: "Active", ReversalOf: null });
        if (entry is not null)
        {
            if (entry.Posting == "Posted")
                await ledger.ReverseAsync(clientId, entry.Id, new ReverseRequest(movement.EffectiveDate, reason ?? $"Voided movement {movementId}"), ct);
            else
                await ledger.VoidAsync(clientId, entry.Id, new VoidRequest(reason ?? $"Voided movement {movementId}"), ct);
        }

        await movements.VoidAsync(clientId, movementId, ct);
        return (await movements.GetAsync(clientId, movementId, ct))!;
    }

    private static void ValidateShape(RecordMovement r)
    {
        switch (r.Type)
        {
            case MovementType.Receipt:
                if (r.Quantity <= 0m) throw new ArgumentException("Receipt quantity must be positive.");
                if (r.UnitCost is not { } c || c < 0m) throw new ArgumentException("Receipt requires a non-negative unit cost.");
                break;
            case MovementType.Issue:
                if (r.Quantity <= 0m) throw new ArgumentException("Issue quantity must be positive.");
                break;
            case MovementType.Adjustment:
                if (r.Quantity == 0m) throw new ArgumentException("Adjustment quantity must be non-zero.");
                if (r.Quantity > 0m && (r.UnitCost is not { } uc || uc < 0m))
                    throw new ArgumentException("An increase adjustment requires a non-negative unit cost.");
                break;
        }
    }
}
